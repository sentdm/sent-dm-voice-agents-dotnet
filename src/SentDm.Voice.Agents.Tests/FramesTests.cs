// The generated frames against the protocol's own fixtures, copied here by the lane at render time.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace SentDm.Voice.Agents.Tests;

public class FramesTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static IEnumerable<object[]> Fixtures(string kind) =>
        Directory
            .GetFiles(Path.Combine(Root, kind), "*.json", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => new object[] { Path.GetRelativePath(Root, file) });

    public static IEnumerable<object[]> Valid => Fixtures("valid");
    public static IEnumerable<object[]> Invalid => Fixtures("invalid");

    private static JsonObject Load(string file) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(Root, file)))!;

    [Theory]
    [MemberData(nameof(Valid))]
    public void ValidFramesRoundTrip(string file)
    {
        var data = Load(file);
        var frame = Protocol.Decode(data);
        Assert.IsNotType<UnknownFrame>(frame);
        Assert.Equal(data["type"]!.GetValue<string>(), frame.Type);
        var again = Protocol.Encode(frame);
        foreach (var (key, value) in data)
            Assert.True(JsonNode.DeepEquals(value, again[key]), $"{key} changed on the way back");
        Assert.True(JsonNode.DeepEquals(again, Protocol.Encode(Protocol.Decode(again))));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidFramesAreRefused(string file)
    {
        var data = Load(file);
        Assert.Throws<FrameException>(() => Protocol.Decode(data));
    }

    [Fact]
    public void FixturesArePresent()
    {
        Assert.NotEmpty(Valid);
        Assert.NotEmpty(Invalid);
    }

    [Fact]
    public void UnknownFramesAndFieldsPass()
    {
        var unknown = Protocol.Decode(
            new JsonObject
            {
                ["type"] = "session.note",
                ["session_id"] = "vs_1",
                ["x"] = 1,
            }
        );
        var frame = Assert.IsType<UnknownFrame>(unknown);
        Assert.Equal("session.note", frame.Type);
        Assert.Equal("vs_1", frame.SessionId);
        Assert.Equal(
            """{"type":"session.note","session_id":"vs_1","x":1}""",
            Protocol.Encode(frame).ToJsonString()
        );

        var said = Assert.IsType<UserSaid>(
            Protocol.Decode(
                """{"type":"user.said","session_id":"vs_1","text":"hi","mood":"good"}"""
            )
        );
        Assert.False(said.Correction);
        var end = Assert.IsType<SessionEnd>(
            Protocol.Decode(
                """{"type":"session.end","session_id":"vs_1","reason":"added_later","duration_ms":1}"""
            )
        );
        Assert.Equal("added_later", end.Reason);
    }

    [Fact]
    public void TheProtocolFactsMatchTheMarks()
    {
        Assert.Equal("sent.voice.v1", Protocol.Subprotocol);
        Assert.Equal(32, Protocol.RetainedFrames);
        Assert.Equal("agent.say", AgentSay.TypeName);
        Assert.Contains("session.start", Protocol.Uncounted);
        Assert.DoesNotContain("agent.say", Protocol.Uncounted);
        Assert.Equal(new HashSet<string> { "text" }, Protocol.SessionTypes["agent.say"]);
        Assert.Contains("pong", Protocol.FromAgent);
        Assert.Contains("ping", Protocol.FromSent);
        Assert.Contains("caller_hung_up", Protocol.SessionEndReasons);
    }

    [Fact]
    public void EncodeRefusesABadFrame()
    {
        Assert.Throws<FrameException>(() =>
            Protocol.Encode(new AgentSay { SessionId = "nope", Text = "hi" })
        );
        Assert.Throws<FrameException>(() =>
            Protocol.Encode(
                new ToolCall
                {
                    SessionId = "vs_1",
                    CallRef = "t1",
                    Name = "transfer",
                }
            )
        );
        Assert.Throws<FrameException>(() => Protocol.Decode("not json"));
    }

    [Fact]
    public void EncodeWritesTypeFirstLeavesNullsOutAndEscapesNothing()
    {
        var json = Protocol.EncodeToString(
            new AgentSay { SessionId = "vs_1", Text = "hi, +1 <you>" }
        );
        Assert.StartsWith("""{"type":"agent.say",""", json);
        Assert.Contains("\"hi, +1 <you>\"", json);
        var call = Protocol.EncodeToString(
            new ToolCall
            {
                SessionId = "vs_1",
                CallRef = "t1",
                Name = "hangup",
            }
        );
        Assert.DoesNotContain("arguments", call);
    }
}
