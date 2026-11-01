// The session's bookkeeping, with a connection that records what it sends: counting both ways, the retained
// frames, what a resume sends again, and tool calls answered by their result.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SentDm.Voice.Agents.Tests;

public class SessionTests
{
    private sealed class Recorder : ISender
    {
        public readonly List<Frame> Frames = new();
        public readonly List<byte[]> Audio = new();

        public void SendText(string type, string json) => Frames.Add(Protocol.Decode(json));

        public void SendBytes(byte[] frame) => Audio.Add(frame);
    }

    private static SessionStart Start(string sessionType = "text", JsonObject? audio = null)
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
            ["session_type"] = sessionType,
            ["started_at"] = "2026-10-31T00:00:00Z",
            ["resume"] = new JsonObject
            {
                ["token"] = "vsr_" + new string('a', 30),
                ["window_ms"] = 15000,
            },
        };
        if (audio is not null)
            data["audio"] = audio;
        return (SessionStart)Protocol.Decode(data);
    }

    private static SessionResumed Resumed(long received, char token) =>
        (SessionResumed)
            Protocol.Decode(
                new JsonObject
                {
                    ["type"] = "session.resumed",
                    ["session_id"] = "vs_1",
                    ["received"] = received,
                    ["resume"] = new JsonObject
                    {
                        ["token"] = "vsr_" + new string(token, 30),
                        ["window_ms"] = 15000,
                    },
                }
            );

    [Fact]
    public void CountsBothWaysAndKeepsTheLatestFrames()
    {
        var recorder = new Recorder();
        var session = new Session(Start(), recorder);
        Assert.True(session.Resumable);
        session.Read(Protocol.Decode("""{"type":"user.said","session_id":"vs_1","text":"hi"}"""));
        session.Read(
            Protocol.Decode("""{"type":"session.resumed","session_id":"vs_1","received":0}""")
        );
        Assert.Equal(1, session.Received);
        for (var i = 0; i < Protocol.RetainedFrames + 5; i++)
            session.Say($"line {i}");
        Assert.Equal(Protocol.RetainedFrames + 5, session.Sent);
        Assert.Equal(Protocol.RetainedFrames + 5, recorder.Frames.Count);
    }

    [Fact]
    public void AResumeSendsAgainWhatSentDidNotRead()
    {
        var session = new Session(Start(), new Recorder());
        session.Say("one");
        session.Say("two");
        session.Say("three");
        var resume = session.ResumeFrame();
        Assert.StartsWith("vsr_", resume.Token);
        Assert.Equal(0, resume.Received);
        var again = session.Resumed(Resumed(1, 'b'));
        Assert.Equal(new[] { "two", "three" }, again.Select(frame => ((AgentSay)frame).Text));
        Assert.StartsWith("vsr_b", session.Token);
        Assert.Equal(3, session.Sent);
    }

    [Fact]
    public void AResumePastTheRetainedFramesHangsUp()
    {
        var session = new Session(Start(), new Recorder());
        for (var i = 0; i < Protocol.RetainedFrames + 3; i++)
            session.Say(i.ToString());
        var again = session.Resumed(Resumed(1, 'c'));
        var call = Assert.IsType<ToolCall>(Assert.Single(again));
        Assert.Equal("hangup", call.Name);
    }

    [Fact]
    public async Task ToolCallsAreAnsweredByTheirResult()
    {
        var recorder = new Recorder();
        var session = new Session(Start(), recorder);
        var task = session.TransferAsync("+14155550199");
        var call = Assert.IsType<ToolCall>(recorder.Frames.Last());
        Assert.Equal("transfer", call.Name);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""{"to":{"kind":"number","value":"+14155550199"}}"""),
                call.Arguments
            )
        );
        session.AnswerTool(
            (ToolResult)
                Protocol.Decode(
                    new JsonObject
                    {
                        ["type"] = "tool.result",
                        ["session_id"] = "vs_1",
                        ["call_ref"] = call.CallRef,
                        ["ok"] = true,
                    }
                )
        );
        Assert.True((await task).Ok);
    }

    [Fact]
    public async Task ASessionThatEndedRefusesMore()
    {
        var session = new Session(Start(), new Recorder());
        var task = session.HangupAsync();
        session.Finish(
            (SessionEnd)
                Protocol.Decode(
                    """{"type":"session.end","session_id":"vs_1","reason":"caller_hung_up","duration_ms":10}"""
                )
        );
        await Assert.ThrowsAsync<SessionEndedException>(() => task);
        Assert.Throws<SessionEndedException>(() => session.Say("too late"));
    }

    [Fact]
    public void AudioGoesOutOnTheSessionStream()
    {
        var recorder = new Recorder();
        var session = new Session(Start("audio", new JsonObject { ["stream"] = 7 }), recorder);
        var samples = new byte[640];
        Array.Fill(samples, (byte)1);
        session.SendAudio(samples);
        Assert.Equal(new byte[] { 0, 7 }, recorder.Audio[0].Take(2));
        Assert.Equal(642, recorder.Audio[0].Length);
    }
}
