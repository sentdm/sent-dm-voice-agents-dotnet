// One session: a call while this agent is on it.
//
// A Session is what the handlers of an Agent receive. It carries what session.start said about the call, sends
// the agent's frames on whichever connection the session is on, keeps the latest counted frames so a resume can
// send again the ones Sent did not read, and turns a tool call into the tool.result that answers it.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SentDm.Voice.Agents;

/// <summary>The session ended before the tool call was answered.</summary>
public sealed class SessionEndedException : Exception
{
    public string SessionId { get; }

    public SessionEndedException(string sessionId)
        : base($"session {sessionId} has ended")
    {
        SessionId = sessionId;
    }
}

/// <summary>The connection a session sends on: the agent gives each session one, and moves it on a resume.</summary>
internal interface ISender
{
    void SendText(string type, string json);
    void SendBytes(byte[] frame);
}

/// <summary>The call the agent is on, and the ways to act on it. Created by the agent; its handlers run in order.</summary>
public sealed class Session
{
    public string Id { get; }
    public string CallId { get; }
    public string AgentId { get; }

    /// <summary><c>inbound</c> or <c>outbound</c>.</summary>
    public string Direction { get; }
    public Party From { get; }
    public Party To { get; }

    /// <summary><c>text</c> or <c>audio</c>.</summary>
    public string SessionType { get; }
    public string? Language { get; }
    public JsonObject Context { get; }
    public string StartedAt { get; }

    /// <summary>Hz of the session's 16-bit mono PCM, both ways, in an audio session.</summary>
    public int SampleRate { get; }

    /// <summary>The number in the header of the session's binary audio frames on its current connection.</summary>
    public int? Stream { get; private set; }

    /// <summary>The latest resume token, or null when the session cannot be resumed. Keep it out of logs.</summary>
    public string? Token { get; private set; }
    public long? WindowMs { get; }

    /// <summary>How many of Sent's counted frames for the session the agent has read: the <c>received</c> of a resume.</summary>
    public long Received { get; private set; }

    /// <summary>session.end, once it arrived.</summary>
    public SessionEnd? End { get; private set; }

    /// <summary>What session.start said about the call.</summary>
    public SessionStart Start { get; }

    internal ISender Connection { get; set; }

    /// <summary>A session.resume was sent on the current connection and Sent has not answered it yet.</summary>
    internal bool Resuming { get; set; }

    /// <summary>The handlers run one after another on this chain.</summary>
    internal Task Chain = Task.CompletedTask;
    internal readonly object ChainGate = new();

    private readonly object _gate = new();
    private List<Frame> _kept = new();
    private long _before;
    private int _calls;
    private readonly Dictionary<string, TaskCompletionSource<ToolResult>> _pending = new();

    internal Session(SessionStart start, ISender connection)
    {
        Start = start;
        Id = start.SessionId;
        CallId = start.CallId;
        AgentId = start.AgentId;
        Direction = start.Direction;
        From = start.From;
        To = start.To;
        SessionType = start.SessionType;
        Language = start.Language;
        Context = start.Context is null ? new JsonObject() : (JsonObject)start.Context.DeepClone();
        StartedAt = start.StartedAt;
        SampleRate = (int)(start.Audio?.SampleRate ?? 16000);
        Stream = start.Audio is null ? null : (int)start.Audio.Stream;
        Token = start.Resume?.Token;
        WindowMs = start.Resume?.WindowMs;
        Connection = connection;
    }

    // ----- what the agent does on the call -----

    public bool Ended => End is not null;

    /// <summary>Whether the session survives a lost connection: it has a resume token.</summary>
    public bool Resumable => Token is not null;

    /// <summary>How many counted frames the agent has sent for the session.</summary>
    public long Sent
    {
        get
        {
            lock (_gate)
                return _before + _kept.Count;
        }
    }

    /// <summary>
    /// Text sessions: speak <paramref name="text"/> to the caller. Send a reply in pieces with <c>last: false</c>
    /// and finish with <c>last: true</c>; the pieces are spoken as one utterance.
    /// </summary>
    public void Say(string text, bool? last = null, bool? interruptible = null)
    {
        Send(
            new AgentSay
            {
                SessionId = Id,
                Text = text,
                Last = last ?? true,
                Interruptible = interruptible ?? true,
            }
        );
    }

    /// <summary>Drop the speech or audio the agent sent that Sent has not played yet.</summary>
    public void Clear() => Send(new AgentClear { SessionId = Id });

    /// <summary>
    /// Audio sessions: play <paramref name="samples"/>, 16-bit little-endian mono PCM at the session's sample
    /// rate. Send 20 ms per call, and no more than 200 ms.
    /// </summary>
    public void SendAudio(ReadOnlySpan<byte> samples)
    {
        if (Stream is not int stream)
            throw new InvalidOperationException($"session {Id} is a text session");
        Connection.SendBytes(Audio.Frame(stream, samples));
    }

    /// <summary>Move the call to <paramref name="to"/>. On success the session then ends with <c>transferred</c>.</summary>
    public Task<ToolResult> TransferAsync(Party to)
    {
        var party = new JsonObject { ["kind"] = to.Kind };
        if (to.Value is not null)
            party["value"] = to.Value;
        return ToolAsync("transfer", new JsonObject { ["to"] = party });
    }

    /// <summary>Move the call to an E.164 number. On success the session then ends with <c>transferred</c>.</summary>
    public Task<ToolResult> TransferAsync(string number) =>
        TransferAsync(new Party { Kind = "number", Value = number });

    /// <summary>End the call, after the speech the agent queued before it.</summary>
    public Task<ToolResult> HangupAsync() => ToolAsync("hangup");

    /// <summary>Play <paramref name="digits"/> on the call: 0 to 9, *, #, A to D, and w to wait half a second.</summary>
    public Task<ToolResult> SendDtmfAsync(string digits) =>
        ToolAsync("send_dtmf", new JsonObject { ["digits"] = digits });

    /// <summary>Put the caller on hold, or take them off it.</summary>
    public Task<ToolResult> HoldAsync(bool held = true) =>
        ToolAsync("hold", new JsonObject { ["held"] = held });

    /// <summary>
    /// Call a tool by name and wait for its tool.result. Faults with SessionEndedException if the session ends
    /// first.
    /// </summary>
    public Task<ToolResult> ToolAsync(string name, JsonObject? arguments = null)
    {
        if (Ended)
            return Task.FromException<ToolResult>(new SessionEndedException(Id));
        var completion = new TaskCompletionSource<ToolResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        string callRef;
        lock (_gate)
        {
            callRef = $"t{++_calls}";
            _pending[callRef] = completion;
        }
        try
        {
            Send(
                new ToolCall
                {
                    SessionId = Id,
                    CallRef = callRef,
                    Name = name,
                    Arguments = arguments,
                }
            );
        }
        catch (Exception error)
        {
            lock (_gate)
                _pending.Remove(callRef);
            completion.TrySetException(error);
        }
        return completion.Task;
    }

    // ----- what the agent's connection does with the session -----

    /// <summary>Sends a frame for the session on its connection, keeping it if it counts.</summary>
    internal void Send(Frame frame)
    {
        if (Ended)
            throw new SessionEndedException(Id);
        var json = Protocol.EncodeToString(frame);
        Keep(frame);
        Connection.SendText(frame.Type, json);
    }

    private void Keep(Frame frame)
    {
        if (Protocol.Uncounted.Contains(frame.Type))
            return;
        lock (_gate)
        {
            _kept.Add(frame);
            if (_kept.Count > Protocol.RetainedFrames)
            {
                _kept.RemoveAt(0);
                _before++;
            }
        }
    }

    /// <summary>Counts one of Sent's frames for the session, as Counting frames in docs/protocol.md says.</summary>
    internal void Read(Frame frame)
    {
        if (!Protocol.Uncounted.Contains(frame.Type))
            lock (_gate)
                Received++;
    }

    internal SessionResume ResumeFrame()
    {
        if (Token is not string token)
            throw new InvalidOperationException($"session {Id} has no resume token");
        return new SessionResume
        {
            SessionId = Id,
            Token = token,
            Received = Received,
        };
    }

    /// <summary>
    /// Takes the answer to a session.resume. Returns the frames to send again, or a hangup when Sent needs one
    /// the session no longer keeps.
    /// </summary>
    internal IReadOnlyList<Frame> Resumed(SessionResumed frame)
    {
        Resuming = false;
        Token = frame.Resume?.Token;
        if (frame.Audio is not null)
            Stream = (int)frame.Audio.Stream;
        // Without a next token the session ended while it was detached; its replay ends with session.end.
        if (frame.Resume is null)
            return Array.Empty<Frame>();
        List<Frame> again;
        lock (_gate)
        {
            if (frame.Received < _before)
                again = new List<Frame>
                {
                    new ToolCall
                    {
                        SessionId = Id,
                        CallRef = $"t{++_calls}",
                        Name = "hangup",
                    },
                };
            else
                again = _kept.GetRange(
                    (int)(frame.Received - _before),
                    _kept.Count - (int)(frame.Received - _before)
                );
            _before = frame.Received;
            _kept = new List<Frame>();
        }
        foreach (var frameAgain in again)
            Keep(frameAgain);
        return again;
    }

    internal void AnswerTool(ToolResult result)
    {
        TaskCompletionSource<ToolResult>? pending;
        lock (_gate)
        {
            if (!_pending.Remove(result.CallRef, out pending))
                return;
        }
        pending.TrySetResult(result);
    }

    internal void Finish(SessionEnd end)
    {
        List<TaskCompletionSource<ToolResult>> pending;
        lock (_gate)
        {
            End = end;
            pending = new List<TaskCompletionSource<ToolResult>>(_pending.Values);
            _pending.Clear();
        }
        foreach (var completion in pending)
            completion.TrySetException(new SessionEndedException(Id));
    }
}
