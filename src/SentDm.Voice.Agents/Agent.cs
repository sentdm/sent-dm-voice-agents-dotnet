// The agent: one process that serves calls on the Sent Voice Agents protocol.
//
// Subclass Agent and override the handlers you need; RunAsync() connects to Sent with the account API key and
// serves sessions until Stop(). The runtime answers heartbeats, opens a new connection when one drains or is
// lost, and resumes the sessions that can move, so the handlers only see calls. Each session's handlers run
// one after another in the order Sent's frames arrived; different sessions run at the same time.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SentDm.Voice.Agents;

/// <summary>Sent refused the connection for a reason that needs a person: the status and why.</summary>
public sealed class RefusedException : Exception
{
    public int Status { get; }
    public string Reason { get; }

    public RefusedException(int status, string reason)
        : base($"{status}: {reason}")
    {
        Status = status;
        Reason = reason;
    }
}

/// <summary>Where the runtime's log lines go. Tokens and keys never appear in them.</summary>
public interface IAgentLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>The default logger: standard output for information, standard error for warnings and errors.</summary>
public sealed class ConsoleLogger : IAgentLogger
{
    public void Info(string message) => Console.Out.WriteLine(message);

    public void Warn(string message) => Console.Error.WriteLine(message);

    public void Error(string message) => Console.Error.WriteLine(message);
}

public sealed class AgentOptions
{
    /// <summary>The account API key. Keep it on the server the agent runs on.</summary>
    public required string ApiKey { get; init; }

    /// <summary>The agent, as <c>POST /v3/voice/agents</c> created it.</summary>
    public required string AgentId { get; init; }

    /// <summary>Where to connect. The default is Sent.</summary>
    public string? Url { get; init; }

    /// <summary>Where the runtime's log lines go. The default is the console.</summary>
    public IAgentLogger? Logger { get; init; }
}

/// <summary>One WebSocket to Sent, and the sessions it reads and writes.</summary>
internal sealed class Connection : ISender
{
    public ClientWebSocket? Socket;
    public bool Ready;
    public bool Draining;
    public readonly Dictionary<string, Session> Sessions = new();
    public List<Session> Pending;
    public Timer? Watchdog;

    /// <summary>What the watchdog waits for, and how long: connection.ready on a new socket, then any frame.</summary>
    public string Expected = "connection.ready";
    public long DueMs;
    public long HeartbeatMs;
    public readonly CancellationTokenSource Cancel = new();
    public readonly Channel<(byte[] Data, WebSocketMessageType Kind)> Outbox =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType)>(
            new UnboundedChannelOptions { SingleReader = true }
        );
    private readonly IAgentLogger _log;

    public Connection(List<Session> pending, IAgentLogger log)
    {
        Pending = pending;
        _log = log;
    }

    public bool Open => Socket?.State == WebSocketState.Open;

    public void SendText(string type, string json)
    {
        if (
            Open
            && Outbox.Writer.TryWrite((Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text))
        )
            return;
        // A session frame is still counted as sent. Sent's received on the next resume says to send it again.
        _log.Info($"not sent: {type}, the connection is not open");
    }

    public void SendBytes(byte[] frame)
    {
        if (Open)
            Outbox.Writer.TryWrite((frame, WebSocketMessageType.Binary));
    }
}

/// <summary>Serves calls. Override the handlers; they run on the session's own chain, in order.</summary>
public class Agent
{
    /// <summary>Where the agent connects when AgentOptions.Url is not set.</summary>
    public static readonly string DefaultUrl = $"wss://{Protocol.Host}{Protocol.ConnectPath}";

    /// <summary>The longest wait before another attempt to connect.</summary>
    private const int MaxBackoffMs = 30_000;

    /// <summary>How long a handshake may take before the attempt counts as failed, and how long an open socket may wait for connection.ready.</summary>
    private const int HandshakeTimeoutMs = 10_000;

    /// <summary>How many heartbeat intervals without a frame make a connection dead, as Heartbeats in docs/protocol.md says.</summary>
    private const int SilentIntervals = 3;

    /// <summary>Why Sent refuses a handshake with each status, from Connecting in docs/protocol.md. Each needs a person.</summary>
    private static readonly Dictionary<int, string> Refusals = new()
    {
        [400] = "agent_id or the subprotocol is missing",
        [401] = "the API key is not valid",
        [403] = "voice is not enabled for the account, or the key does not own the agent",
        [404] = "the agent does not exist",
    };

    public string ApiKey { get; }
    public string AgentId { get; }
    public string Url { get; }
    protected IAgentLogger Log { get; }

    private readonly object _gate = new();
    internal readonly HashSet<Connection> _connections = new();
    internal readonly Dictionary<string, Session> _live = new();
    internal int _attempt;
    private bool _stopped;
    private TaskCompletionSource? _done;

    public Agent(AgentOptions options)
    {
        ApiKey = options.ApiKey;
        AgentId = options.AgentId;
        Url = options.Url ?? DefaultUrl;
        Log = options.Logger ?? new ConsoleLogger();
    }

    // ----- handlers to override -----

    /// <summary>A connection to Sent is ready for sessions.</summary>
    public virtual Task OnConnectedAsync(ConnectionReady ready) => Task.CompletedTask;

    /// <summary>A call reached the agent. In a text session, Say() to speak; an audio session waits for audio.</summary>
    public virtual Task OnSessionStartAsync(Session session) => Task.CompletedTask;

    /// <summary>Text sessions: the caller said something. With Correction true it replaces the previous one.</summary>
    public virtual Task OnUserSaidAsync(Session session, UserSaid said) => Task.CompletedTask;

    /// <summary>Audio sessions: the caller's audio, 16-bit little-endian mono PCM at session.SampleRate.</summary>
    public virtual Task OnAudioAsync(Session session, ReadOnlyMemory<byte> samples) =>
        Task.CompletedTask;

    /// <summary>Sent answered a tool call. The call's awaiting task gets the same result.</summary>
    public virtual Task OnToolResultAsync(Session session, ToolResult result) => Task.CompletedTask;

    /// <summary>A session frame this SDK version does not know. It was counted; nothing else is needed.</summary>
    public virtual Task OnFrameAsync(Session session, UnknownFrame frame) => Task.CompletedTask;

    /// <summary>The call left the agent. end.Reason says why.</summary>
    public virtual Task OnSessionEndAsync(Session session, SessionEnd end) => Task.CompletedTask;

    /// <summary>The session's connection was lost and it could not be resumed: Sent ends the call with agent_unreachable.</summary>
    public virtual Task OnSessionLostAsync(Session session) => Task.CompletedTask;

    /// <summary>Sent refused a frame; the connection stays open.</summary>
    public virtual Task OnErrorAsync(ErrorFrame error) => Task.CompletedTask;

    // ----- running -----

    /// <summary>
    /// Serves calls until Stop(). Completes after Stop(); faults with RefusedException when Sent refuses the
    /// connection for a reason that needs a person.
    /// </summary>
    public Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _done = done;
        OpenConnection(new List<Session>(), 0);
        return done.Task;
    }

    /// <summary>Runs the agent until SIGINT or SIGTERM. For a program whose main is the agent.</summary>
    public Task ServeAsync()
    {
        var registrations = new[]
        {
            PosixSignalRegistration.Create(
                PosixSignal.SIGINT,
                context =>
                {
                    context.Cancel = true;
                    Stop();
                }
            ),
            PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    Stop();
                }
            ),
        };
        return RunAsync()
            .ContinueWith(
                run =>
                {
                    foreach (var registration in registrations)
                        registration.Dispose();
                    return run;
                },
                TaskScheduler.Default
            )
            .Unwrap();
    }

    /// <summary>Stops serving: every connection closes with 1000, so the calls on them follow the agent's fallback at once.</summary>
    public void Stop()
    {
        List<Connection> connections;
        TaskCompletionSource? done;
        lock (_gate)
        {
            _stopped = true;
            connections = _connections.ToList();
            done = _done;
            _done = null;
        }
        foreach (var connection in connections)
        {
            connection.Watchdog?.Dispose();
            var socket = connection.Socket;
            if (
                socket is not null
                && (
                    socket.State == WebSocketState.Open
                    || socket.State == WebSocketState.CloseReceived
                )
            )
                _ = CloseQuietly(socket);
            else
                connection.Cancel.Cancel();
        }
        done?.TrySetResult();
    }

    private static async Task CloseQuietly(ClientWebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(5_000);
            await socket
                .CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            socket.Abort();
        }
    }

    /// <summary>Every live session.</summary>
    public IReadOnlyList<Session> Sessions
    {
        get
        {
            lock (_gate)
                return _live.Values.ToList();
        }
    }

    // ----- connections -----

    private void OpenConnection(List<Session> pending, int delayMs)
    {
        var connection = new Connection(pending, Log);
        lock (_gate)
            _connections.Add(connection);
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs, connection.Cancel.Token).ConfigureAwait(false);
                await Dial(connection).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Closed(connection);
            }
            catch (Exception error)
            {
                Log.Error($"the connection failed: {error}");
                Closed(connection);
            }
        });
    }

    private async Task Dial(Connection connection)
    {
        var builder = new UriBuilder(Url);
        var query = builder.Query.TrimStart('?');
        builder.Query =
            (query.Length > 0 ? query + "&" : "") + "agent_id=" + Uri.EscapeDataString(AgentId);
        var url = builder.Uri;
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(Protocol.Subprotocol);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ApiKey}");
        socket.Options.CollectHttpResponseDetails = true;
        connection.Socket = socket;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(
                connection.Cancel.Token
            );
            handshake.CancelAfter(HandshakeTimeoutMs);
            await socket.ConnectAsync(url, handshake.Token).ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is WebSocketException or OperationCanceledException or IOException)
        {
            var status = (int)socket.HttpStatusCode;
            if (Refusals.TryGetValue(status, out var reason))
            {
                Log.Error($"refused with {status}: {reason}");
                TaskCompletionSource? done;
                lock (_gate)
                {
                    _stopped = true;
                    done = _done;
                    _done = null;
                }
                done?.TrySetException(new RefusedException(status, reason));
            }
            else
                Log.Warn(
                    status > 0
                        ? $"refused with {status}: trying again"
                        : $"connect failed: {error.Message}"
                );
            socket.Dispose();
            Closed(connection);
            return;
        }
        Log.Info($"connected to {url.Host}");
        // Until connection.ready arrives the socket has the handshake's deadline; Ready() hands it to Heard().
        Arm(connection, HandshakeTimeoutMs, "connection.ready");
        var writer = Write(connection, socket);
        try
        {
            await Read(connection, socket).ConfigureAwait(false);
        }
        finally
        {
            // Whatever ended the read, the socket is released and the connection reported closed.
            connection.Outbox.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The writer's own failures are the socket's; nothing to add.
            }
            socket.Dispose();
            Closed(connection);
        }
    }

    private async Task Write(Connection connection, ClientWebSocket socket)
    {
        try
        {
            await foreach (
                var (data, kind) in connection
                    .Outbox.Reader.ReadAllAsync(connection.Cancel.Token)
                    .ConfigureAwait(false)
            )
                await socket
                    .SendAsync(data, kind, true, connection.Cancel.Token)
                    .ConfigureAwait(false);
        }
        catch (Exception error)
            when (error
                    is WebSocketException
                        or OperationCanceledException
                        or ObjectDisposedException
                        or InvalidOperationException
            )
        {
            // The socket is closing or closed; the reader reports it.
        }
    }

    private async Task Read(Connection connection, ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        string closed = "closed";
        try
        {
            while (true)
            {
                var result = await socket
                    .ReceiveAsync(buffer, connection.Cancel.Token)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closed =
                        $"closed with {(int?)socket.CloseStatus ?? 1005} {socket.CloseStatusDescription}".TrimEnd();
                    try
                    {
                        await socket
                            .CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure,
                                null,
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // The other side is gone; nothing to answer.
                    }
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                var bytes = message.ToArray();
                message.SetLength(0);
                Heard(connection);
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    HandleAudio(connection, bytes);
                    continue;
                }
                Frame frame;
                try
                {
                    frame = Protocol.Decode(Encoding.UTF8.GetString(bytes));
                }
                catch (FrameException error)
                {
                    Log.Warn($"ignored a frame: {error.Message}");
                    continue;
                }
                try
                {
                    Handle(connection, frame);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // A frame the runtime could not handle is skipped; the connection goes on.
                    Log.Error($"handling a {frame.Type} frame failed: {error}");
                }
            }
        }
        catch (Exception error)
            when (error
                    is WebSocketException
                        or OperationCanceledException
                        or ObjectDisposedException
                        or IOException
            )
        {
            closed =
                error is OperationCanceledException
                    ? "closed by the agent"
                    : $"lost: {error.Message}";
        }
        Log.Info(closed);
    }

    /// <summary>Resets the watchdog: no frame for three heartbeat intervals means the connection is dead.</summary>
    private void Heard(Connection connection)
    {
        if (!connection.Ready)
            return;
        Arm(connection, SilentIntervals * connection.HeartbeatMs, "frame");
    }

    /// <summary>Arms the connection's one watchdog: nothing within <paramref name="dueMs"/> closes the socket.</summary>
    private void Arm(Connection connection, long dueMs, string expected)
    {
        connection.Expected = expected;
        connection.DueMs = dueMs;
        connection.Watchdog ??= new Timer(
            _ =>
            {
                Log.Warn($"no {connection.Expected} for {connection.DueMs} ms, closing");
                connection.Socket?.Abort();
            },
            null,
            Timeout.Infinite,
            Timeout.Infinite
        );
        connection.Watchdog.Change(dueMs, Timeout.Infinite);
    }

    internal void Handle(Connection connection, Frame frame)
    {
        switch (frame)
        {
            case ConnectionReady ready:
                Ready(connection, ready);
                return;
            case Ping ping:
                connection.SendText(
                    Pong.TypeName,
                    Protocol.EncodeToString(new Pong { Ts = ping.Ts })
                );
                // A connection that lived through a heartbeat interval is a good one: the next loss starts the
                // backoff over. A connection that dies before its first ping keeps it growing.
                if (connection.Ready)
                    lock (_gate)
                        _attempt = 0;
                return;
            case ConnectionDraining:
                Drain(connection);
                return;
            case SessionStart start:
                StartSession(connection, start);
                return;
            case SessionResumed resumed:
                Resumed(connection, resumed);
                return;
            case ErrorFrame error:
                Failed(connection, error);
                _ = Safely(() => OnErrorAsync(error));
                return;
            case ISessionFrame scoped:
                SessionFrame(connection, frame, scoped.SessionId);
                return;
            case UnknownFrame unknown when unknown.SessionId is string sessionId:
                SessionFrame(connection, frame, sessionId);
                return;
            default:
                Log.Info($"ignored a {frame.Type} frame");
                return;
        }
    }

    internal void Ready(Connection connection, ConnectionReady frame)
    {
        List<Session> pending;
        lock (_gate)
        {
            connection.Ready = true;
            connection.HeartbeatMs = frame.HeartbeatIntervalMs;
            pending = connection
                .Pending.Where(session =>
                    _live.TryGetValue(session.Id, out var live) && live == session
                )
                .ToList();
            connection.Pending = new List<Session>();
        }
        Heard(connection);
        Log.Info(
            $"ready: protocol {frame.ProtocolVersion}, up to {frame.MaxSessions} sessions, resuming {pending.Count}"
        );
        foreach (var session in pending)
            Resume(connection, session);
        _ = Safely(() => OnConnectedAsync(frame));
    }

    private void Resume(Connection connection, Session session)
    {
        lock (_gate)
        {
            foreach (var other in _connections)
                other.Sessions.Remove(session.Id);
            connection.Sessions[session.Id] = session;
            session.Connection = connection;
            session.Resuming = true;
        }
        var resume = session.ResumeFrame();
        connection.SendText(resume.Type, Protocol.EncodeToString(resume));
    }

    private void Drain(Connection connection)
    {
        List<Session> moving;
        lock (_gate)
        {
            connection.Draining = true;
            moving = connection
                .Sessions.Values.Where(session => session.Token is not null && !session.Resuming)
                .ToList();
        }
        Log.Info($"draining, moving {moving.Count} session(s)");
        OpenConnection(moving, 0);
    }

    internal void StartSession(Connection connection, SessionStart frame)
    {
        var session = new Session(frame, connection);
        lock (_gate)
        {
            _live[session.Id] = session;
            connection.Sessions[session.Id] = session;
        }
        Log.Info($"session {session.Id} started: {frame.SessionType}, {frame.Direction}");
        Enqueue(session, () => OnSessionStartAsync(session));
    }

    private void SessionFrame(Connection connection, Frame frame, string sessionId)
    {
        Session? session;
        lock (_gate)
        {
            // A session that moved to another connection is read there: Sent replays what this one did not deliver.
            if (!connection.Sessions.TryGetValue(sessionId, out session) || session.Resuming)
                return;
        }
        session.Read(frame);
        switch (frame)
        {
            case UserSaid said:
                Enqueue(session, () => OnUserSaidAsync(session, said));
                return;
            case ToolResult result:
                session.AnswerTool(result);
                Enqueue(session, () => OnToolResultAsync(session, result));
                return;
            case SessionEnd end:
                session.Finish(end);
                Forget(session);
                Enqueue(session, () => OnSessionEndAsync(session, end));
                return;
            case UnknownFrame unknown:
                Enqueue(session, () => OnFrameAsync(session, unknown));
                return;
            default:
                return;
        }
    }

    private void HandleAudio(Connection connection, byte[] message)
    {
        if (message.Length < Audio.HeaderBytes)
            return;
        var stream = Audio.StreamOf(message);
        Session? session;
        lock (_gate)
            session = connection.Sessions.Values.FirstOrDefault(s =>
                s.Stream == stream && !s.Resuming
            );
        if (session is null)
            return;
        var samples = Audio.SamplesOf(message);
        Enqueue(session, () => OnAudioAsync(session, samples));
    }

    private void Resumed(Connection connection, SessionResumed frame)
    {
        Session? session;
        lock (_gate)
        {
            if (!connection.Sessions.TryGetValue(frame.SessionId, out session) || !session.Resuming)
                return;
        }
        var again = session.Resumed(frame);
        Log.Info($"session {session.Id} resumed, sent {again} frame(s) again");
    }

    /// <summary>
    /// An error is uncounted, as Counting frames in docs/protocol.md says. Only one that refuses a session.resume
    /// changes the session's fate; any other is the handler's to read.
    /// </summary>
    internal void Failed(Connection connection, ErrorFrame frame)
    {
        Log.Warn($"error {frame.Error.Code}: {frame.Error.Message}");
        if (
            frame.FrameType != "session.resume"
            && frame.Error.Code is not ("resume_failed" or "connection_full")
        )
            return;
        Session? session;
        lock (_gate)
        {
            if (
                frame.SessionId is null
                || !connection.Sessions.TryGetValue(frame.SessionId, out session)
                || !session.Resuming
            )
                return;
            session.Resuming = false;
            connection.Sessions.Remove(session.Id);
        }
        // This connection already carries max_sessions. The token is still good on another one.
        if (frame.Error.Code == "connection_full")
        {
            OpenConnection(new List<Session> { session }, 0);
            return;
        }
        Lost(session);
    }

    private void Forget(Session session)
    {
        lock (_gate)
        {
            _live.Remove(session.Id);
            foreach (var connection in _connections)
                connection.Sessions.Remove(session.Id);
        }
    }

    internal void Lost(Session session)
    {
        Forget(session);
        session.Abandon();
        Enqueue(session, () => OnSessionLostAsync(session));
    }

    internal void Closed(Connection connection)
    {
        List<Session> pending;
        int delay;
        bool drained;
        var lostSessions = new List<Session>();
        lock (_gate)
        {
            if (!_connections.Remove(connection))
                return;
            connection.Watchdog?.Dispose();
            if (_stopped)
                return;
            bool Queued(Session session) =>
                _connections.Any(other => other.Pending.Contains(session));
            var lost = connection.Sessions.Values.ToList();
            var movable = lost.Where(session =>
                    session.Token is not null && !session.Resuming && !Queued(session)
                )
                .ToList();
            foreach (var session in lost)
            {
                if (movable.Contains(session) || Queued(session))
                    continue;
                Log.Warn(
                    $"session {session.Id} lost: {(session.Token is not null ? "its resume was not answered" : "it has no resume token")}"
                );
                lostSessions.Add(session);
            }
            pending = connection
                .Pending.Where(session =>
                    _live.TryGetValue(session.Id, out var live) && live == session
                )
                .Concat(movable)
                .ToList();
            drained = connection.Draining && pending.Count == 0;
            delay = drained ? 0 : Backoff();
        }
        // Reported whether or not a reconnect follows: a drained connection's leftovers are lost too.
        foreach (var session in lostSessions)
            Lost(session);
        if (drained)
            return;
        Log.Info($"reconnecting in {delay} ms to resume {pending.Count} session(s)");
        OpenConnection(pending, delay);
    }

    private int Backoff()
    {
        var delay =
            _attempt == 0 ? 0 : (int)Math.Min(MaxBackoffMs, 1000L << Math.Min(_attempt - 1, 20));
        _attempt++;
        return delay;
    }

    // ----- the session's chain -----

    private void Enqueue(Session session, Func<Task> handler)
    {
        lock (session.ChainGate)
            session.Chain = session
                .Chain.ContinueWith(_ => Safely(handler), TaskScheduler.Default)
                .Unwrap();
    }

    /// <summary>Runs a handler; an exception in it is logged and does not take the session or the agent down.</summary>
    private async Task Safely(Func<Task> handler)
    {
        try
        {
            await handler().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Error($"a handler failed: {error}");
        }
    }
}
