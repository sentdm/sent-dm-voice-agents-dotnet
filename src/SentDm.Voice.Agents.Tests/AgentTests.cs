// The agent's bookkeeping around a connection, without a socket: which errors refuse a resume, when the
// backoff starts over, what a lost session's chain sees, and what a draining connection reports when it closes.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SentDm.Voice.Agents.Tests;

public class AgentTests
{
    private sealed class Quiet : IAgentLogger
    {
        public void Info(string message) { }

        public void Warn(string message) { }

        public void Error(string message) { }
    }

    private static readonly IAgentLogger Silent = new Quiet();

    private static SessionStart Start(bool resumable = true)
    {
        var data = new JsonObject
        {
            ["type"] = "session.start",
            ["session_id"] = "vs_1",
            ["call_id"] = "call_1",
            ["agent_id"] = "agent_1",
            ["direction"] = "inbound",
            ["from"] = new JsonObject { ["kind"] = "number", ["value"] = "+14155550100" },
            ["to"] = new JsonObject { ["kind"] = "agent", ["value"] = "agent_1" },
            ["session_type"] = "text",
            ["started_at"] = "2026-10-31T00:00:00Z",
        };
        if (resumable)
            data["resume"] = new JsonObject
            {
                ["token"] = "vsr_" + new string('a', 30),
                ["window_ms"] = 15000,
            };
        return (SessionStart)Protocol.Decode(data);
    }

    private static ErrorFrame Error(string? frameType = null, string code = "frame_not_allowed")
    {
        var data = new JsonObject
        {
            ["type"] = "error",
            ["session_id"] = "vs_1",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = "no" },
        };
        if (frameType is not null)
            data["frame_type"] = frameType;
        return (ErrorFrame)Protocol.Decode(data);
    }

    private static (Agent Agent, Connection Connection, Session Session) Resuming()
    {
        var agent = new Agent(
            new AgentOptions
            {
                ApiKey = "k",
                AgentId = "agent_1",
                Logger = Silent,
            }
        );
        var connection = new Connection(new List<Session>(), Silent) { Ready = true };
        var session = new Session(Start(), connection) { Resuming = true };
        agent._live[session.Id] = session;
        connection.Sessions[session.Id] = session;
        agent._connections.Add(connection);
        return (agent, connection, session);
    }

    [Fact]
    public async Task OnlyAnErrorAboutTheResumeRefusesIt()
    {
        var (agent, connection, session) = Resuming();
        agent.Failed(connection, Error(frameType: "agent.say"));
        Assert.True(
            session.Resuming && connection.Sessions.ContainsKey(session.Id) && !session.Lost
        );
        agent.Failed(connection, Error(frameType: "session.resume", code: "resume_failed"));
        Assert.True(
            session.Lost
                && !connection.Sessions.ContainsKey(session.Id)
                && !agent._live.ContainsKey(session.Id)
        );
        await session.Chain;
        agent.Stop();
    }

    [Fact]
    public void AFullConnectionMovesTheResumeElsewhere()
    {
        var (agent, connection, session) = Resuming();
        agent.Failed(connection, Error(code: "connection_full"));
        Assert.False(session.Lost);
        Assert.False(connection.Sessions.ContainsKey(session.Id));
        var opened = agent._connections.Where(c => c != connection).ToList();
        Assert.Single(opened);
        Assert.Equal(new[] { session }, opened[0].Pending);
        agent.Stop();
    }

    [Fact]
    public void TheBackoffStartsOverAfterAHeartbeatNotOnReady()
    {
        var agent = new Agent(
            new AgentOptions
            {
                ApiKey = "k",
                AgentId = "agent_1",
                Logger = Silent,
            }
        );
        var connection = new Connection(new List<Session>(), Silent);
        agent._connections.Add(connection);
        agent._attempt = 3;
        agent.Ready(
            connection,
            (ConnectionReady)
                Protocol.Decode(
                    """{"type":"connection.ready","protocol_version":"0.2.0","heartbeat_interval_ms":15000,"max_sessions":10}"""
                )
        );
        Assert.Equal(3, agent._attempt);
        agent.Handle(connection, Protocol.Decode("""{"type":"ping","ts":1}"""));
        Assert.Equal(0, agent._attempt);
        agent.Stop();
    }

    private sealed class Noticing : Agent
    {
        public readonly List<string> Seen = new();

        public Noticing()
            : base(
                new AgentOptions
                {
                    ApiKey = "k",
                    AgentId = "agent_1",
                    Logger = Silent,
                }
            ) { }

        public override async Task OnSessionStartAsync(Session session)
        {
            try
            {
                await session.HangupAsync();
            }
            catch (SessionEndedException)
            {
                Seen.Add("tool failed");
            }
        }

        public override Task OnSessionLostAsync(Session session)
        {
            Seen.Add("lost");
            throw new InvalidOperationException("thrown before any await");
        }
    }

    [Fact]
    public async Task ALostSessionReachesItsHandlerAfterItsToolCallFails()
    {
        var agent = new Noticing();
        var connection = new Connection(new List<Session>(), Silent);
        agent._connections.Add(connection);
        agent.StartSession(connection, Start());
        var session = Assert.Single(agent.Sessions);
        await Task.Yield();
        agent.Lost(session);
        await session.Chain;
        Assert.Equal(new[] { "tool failed", "lost" }, agent.Seen);
        Assert.Empty(agent.Sessions);
        agent.Stop();
    }

    [Fact]
    public async Task ADrainingConnectionReportsItsLeftoverSessionsLostWhenItCloses()
    {
        var agent = new Noticing();
        var connection = new Connection(new List<Session>(), Silent)
        {
            Ready = true,
            Draining = true,
        };
        agent._connections.Add(connection);
        agent.StartSession(connection, Start(resumable: false));
        var session = Assert.Single(agent.Sessions);
        await Task.Yield();
        agent.Closed(connection);
        await session.Chain;
        Assert.Contains("lost", agent.Seen);
        Assert.True(session.Lost);
        Assert.Empty(agent.Sessions);
        Assert.Empty(agent._connections);
        agent.Stop();
    }
}
