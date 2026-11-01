// The conformance agent of sent-dm-voice-protocol's conformance/README.md, on this SDK's API. The protocol's
// harness runs it in CI before every release, through the SentDm.Voice.Agents.Conformance program: it connects
// to SENT_CONFORMANCE_URL with any API key and does exactly what the README's table says.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SentDm.Voice.Agents.Conformance;

public sealed class ConformanceAgent : Agent
{
    public const string Greeting = "Hello from the conformance agent.";

    public abstract record Step;

    public sealed record Say(string Text, bool? Last = null) : Step;

    public sealed record Tool(string Name, JsonObject? Arguments = null) : Step;

    public ConformanceAgent(string url)
        : base(
            new AgentOptions
            {
                ApiKey = "conformance",
                AgentId = "agent_conformance",
                Url = url,
            }
        ) { }

    /// <summary>
    /// What a user.said asks for: the actions the agent performs in order. A command is the text up to the first
    /// space; <c>stream</c> takes the next word as the first piece.
    /// </summary>
    public static IReadOnlyList<Step> Decide(string text)
    {
        var space = text.IndexOf(' ');
        var command = space < 0 ? text : text.Substring(0, space);
        var rest = space < 0 ? "" : text.Substring(space + 1);
        switch (command)
        {
            case "echo":
                return new Step[] { new Say(rest) };
            case "stream":
            {
                var next = rest.IndexOf(' ');
                var word = next < 0 ? rest : rest.Substring(0, next);
                var remainder = next < 0 ? "" : rest.Substring(next + 1);
                return new Step[] { new Say(word, false), new Say(remainder) };
            }
            case "transfer":
                return new Step[]
                {
                    new Tool(
                        "transfer",
                        new JsonObject
                        {
                            ["to"] = new JsonObject
                            {
                                ["kind"] = "user",
                                ["value"] = "conformance-desk",
                            },
                        }
                    ),
                };
            case "hangup":
                return new Step[] { new Tool("hangup") };
            case "dtmf":
                return new Step[] { new Tool("send_dtmf", new JsonObject { ["digits"] = rest }) };
            case "hold":
                return new Step[] { new Tool("hold", new JsonObject { ["held"] = true }) };
            default:
                return Array.Empty<Step>();
        }
    }

    public override Task OnSessionStartAsync(Session session)
    {
        if (session.SessionType == "text")
            session.Say(Greeting);
        return Task.CompletedTask;
    }

    public override async Task OnUserSaidAsync(Session session, UserSaid said)
    {
        try
        {
            if (said.Correction)
                session.Clear();
            foreach (var step in Decide(said.Text))
            {
                if (step is Say say)
                    session.Say(say.Text, say.Last);
                // The harness answers every tool call; the result also reaches OnToolResultAsync.
                else if (step is Tool tool)
                    await session.ToolAsync(tool.Name, tool.Arguments).ConfigureAwait(false);
            }
        }
        catch (SessionEndedException)
        {
            // The call left while the agent was acting on it.
        }
    }

    public override Task OnToolResultAsync(Session session, ToolResult result)
    {
        if (!result.Ok && result.Error is not null)
        {
            try
            {
                session.Say($"Tool failed: {result.Error.Code}");
            }
            catch (SessionEndedException)
            {
                // The call left first.
            }
        }
        return Task.CompletedTask;
    }

    public override Task OnAudioAsync(Session session, ReadOnlyMemory<byte> samples)
    {
        session.SendAudio(samples.Span);
        return Task.CompletedTask;
    }
}
