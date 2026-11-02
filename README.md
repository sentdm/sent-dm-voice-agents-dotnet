# Sent Voice Agents for .NET

An AI agent that takes and places phone calls through [Sent](https://sent.dm), on the
[Sent Voice Agents protocol](https://github.com/sentdm/sent-dm-voice-protocol) 1.0.0, for .NET 8
and later.

Status: Preview, with the protocol. Generated and rendered by `sent-dm-sdk-pipeline` from the protocol at
`v1.0.0`; every release passes the protocol's conformance scenarios in CI.

Read the [Voice Agents docs](https://docs.sent.dm) to get started.

## Install

```sh
dotnet add package SentDm.Voice.Agents
```

.NET 8 or later. The package depends on nothing outside the runtime: `System.Net.WebSockets` carries the
connection and `System.Text.Json` the frames.

## Quickstart

```csharp
using SentDm.Voice.Agents;

class Support : Agent
{
    public Support(AgentOptions options) : base(options) { }

    public override Task OnSessionStartAsync(Session session)
    {
        session.Say("Hi, this is Acme support. How can I help?");
        return Task.CompletedTask;
    }

    public override async Task OnUserSaidAsync(Session session, UserSaid said)
    {
        if (said.Text.Contains("person"))
        {
            session.Say("Transferring you now.");
            await session.TransferAsync("+14155550100");
        }
        else
        {
            session.Say($"You said {said.Text}");
        }
    }
}

await new Support(new AgentOptions
{
    ApiKey = Environment.GetEnvironmentVariable("SENT_API_KEY")!,
    AgentId = "agent_...",
}).ServeAsync();
```

`ServeAsync()` connects to Sent with the account API key and serves calls until SIGINT or SIGTERM; `RunAsync()`
does the same without the signal handlers, and `Stop()` ends it. Each session's handlers run in order on their
own chain, so sessions never wait for one another. The runtime answers heartbeats, opens a new connection when
one drains or is lost, and resumes the sessions that can move; the handlers only see calls.

| Handler                                  | When                                                                                           |
| ---------------------------------------- | ---------------------------------------------------------------------------------------------- |
| `OnSessionStartAsync(session)`           | A call reached the agent. In a text session, `Say()` to speak                                  |
| `OnUserSaidAsync(session, said)`         | Text sessions: the caller said something; `said.Correction` means it replaces the previous one |
| `OnAudioAsync(session, samples)`         | Audio sessions: the caller's audio, 16-bit mono PCM at `session.SampleRate`                    |
| `OnToolResultAsync(session, result)`     | Sent answered a tool call; the awaiting task gets the same result                              |
| `OnSessionEndAsync(session, end)`        | The call left the agent; `end.Reason` says why                                                 |
| `OnSessionLostAsync(session)`            | The connection was lost and the session could not be resumed                                   |

On a `Session`: `Say(text, last, interruptible)`, `Clear()`, `SendAudio(samples)`, and the tools
`TransferAsync(to)`, `HangupAsync()`, `SendDtmfAsync(digits)`, `HoldAsync(held)`, each completing with Sent's
`ToolResult`. `session.From`, `To`, `Direction`, `SessionType`, `Language`, `Context`, `SampleRate` carry what
`session.start` said.

`Frames.cs` holds every frame as a record, with `Protocol.Decode()` and `Protocol.Encode()` that check the
protocol's rules; it is generated from the protocol and not edited by hand.

## Develop

```sh
dotnet tool restore
dotnet build && dotnet test            # the library, the tests against the protocol's fixtures
dotnet csharpier check .               # formatting; Frames.cs is excluded
```

The conformance scenarios run in CI through the protocol's action, against the
`SentDm.Voice.Agents.Conformance` program. Releases are cut by release-please from `main` and published to
nuget.org from GitHub Actions by trusted publishing, with no stored key.
