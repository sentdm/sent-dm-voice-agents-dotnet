// The conformance agent of sent-dm-voice-protocol, as a program: the protocol's harness runs it with
// SENT_CONFORMANCE_URL set, in CI before every release, and by hand against a harness on localhost.
using System;
using System.Threading.Tasks;
using SentDm.Voice.Agents.Conformance;

var url = Environment.GetEnvironmentVariable("SENT_CONFORMANCE_URL");
await new ConformanceAgent(
    string.IsNullOrEmpty(url) ? "ws://localhost:8765/v1/agents/connect" : url
).ServeAsync();
