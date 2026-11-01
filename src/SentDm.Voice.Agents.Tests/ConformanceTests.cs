using System.Linq;
using SentDm.Voice.Agents.Conformance;

namespace SentDm.Voice.Agents.Tests;

public class ConformanceTests
{
    private static string[] Shape(string text) =>
        ConformanceAgent
            .Decide(text)
            .Select(step =>
                step switch
                {
                    ConformanceAgent.Say say => $"say {say.Text}"
                        + (say.Last is bool last ? $" last={last}" : ""),
                    ConformanceAgent.Tool tool =>
                        $"tool {tool.Name} {tool.Arguments?.ToJsonString() ?? "-"}",
                    _ => "?",
                }
            )
            .ToArray();

    [Fact]
    public void Commands()
    {
        Assert.Equal(new[] { "say hello there" }, Shape("echo hello there"));
        Assert.Equal(new[] { "say Let last=False", "say me think" }, Shape("stream Let me think"));
        Assert.Equal(
            new[] { """tool transfer {"to":{"kind":"user","value":"conformance-desk"}}""" },
            Shape("transfer")
        );
        Assert.Equal(new[] { "tool hangup -" }, Shape("hangup"));
        Assert.Equal(new[] { """tool send_dtmf {"digits":"12#"}""" }, Shape("dtmf 12#"));
        Assert.Equal(new[] { """tool hold {"held":true}""" }, Shape("hold"));
        Assert.Empty(Shape("something else"));
    }
}
