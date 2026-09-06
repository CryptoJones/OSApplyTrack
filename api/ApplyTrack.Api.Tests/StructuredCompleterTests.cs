// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The JSON-out-of-a-chat-model path: extraction tolerates prose and fences, a bad
/// reply gets exactly one repair prompt quoting the problem, and a second bad reply
/// is a hard failure — never a fall-through.
/// </summary>
public class StructuredCompleterTests
{
    private sealed record Answer(string? Decision, int? Confidence);

    private static readonly EffectiveLlmConfig Cfg = new("http://local/v1", "test-model", null, 30);

    private static string? Validate(Answer a) =>
        a.Decision is "proceed" or "skip" ? null : "\"decision\" must be proceed or skip";

    [Theory]
    [InlineData("{\"decision\":\"proceed\",\"confidence\":80}")]
    [InlineData("Sure! Here is my answer:\n```json\n{\"decision\":\"proceed\",\"confidence\":80}\n```\nHope that helps.")]
    [InlineData("<think>hmm {not json} </think>{\"decision\": \"proceed\", \"confidence\": 80, \"note\": \"a } inside a string\"}")]
    public async Task Parses_the_first_balanced_object_out_of_a_chatty_reply(string reply)
    {
        var stub = new StubLlmClient((_, _, _) => reply);
        var completer = new StructuredCompleter(stub);

        var a = await completer.CompleteAsync<Answer>("sys", "user", Cfg, Validate);

        Assert.Equal("proceed", a.Decision);
        Assert.Equal(80, a.Confidence);
        Assert.Equal(1, stub.Calls);
        Assert.Contains("single JSON object", stub.LastSystemPrompt);
    }

    [Fact]
    public async Task A_bad_reply_is_repaired_once_with_the_error_quoted()
    {
        var replies = new Queue<string>(["I'd rather not say.", "{\"decision\":\"skip\",\"confidence\":60}"]);
        var stub = new StubLlmClient((_, _, _) => replies.Dequeue());
        var completer = new StructuredCompleter(stub);

        var a = await completer.CompleteAsync<Answer>("sys", "user", Cfg, Validate);

        Assert.Equal("skip", a.Decision);
        Assert.Equal(2, stub.Calls);
        Assert.Contains("could not be used: no JSON object was found", stub.LastUserPrompt);
        Assert.StartsWith("user", stub.LastUserPrompt); // the original prompt is kept
    }

    [Fact]
    public async Task A_validation_failure_is_quoted_back_to_the_model()
    {
        var replies = new Queue<string>(["{\"decision\":\"maybe\"}", "{\"decision\":\"skip\"}"]);
        var stub = new StubLlmClient((_, _, _) => replies.Dequeue());

        await new StructuredCompleter(stub).CompleteAsync<Answer>("sys", "user", Cfg, Validate);

        Assert.Contains("must be proceed or skip", stub.LastUserPrompt);
    }

    [Fact]
    public async Task Two_bad_replies_are_a_hard_failure_not_a_fall_through()
    {
        var stub = new StubLlmClient((_, _, _) => "nope");

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() =>
            new StructuredCompleter(stub).CompleteAsync<Answer>("sys", "user", Cfg, Validate));

        Assert.Equal(2, stub.Calls);
        Assert.Contains("did not return a usable answer", ex.Message);
    }

    [Fact]
    public void Extraction_returns_null_without_a_complete_object()
    {
        Assert.Null(StructuredCompleter.ExtractFirstObject("no braces here"));
        Assert.Null(StructuredCompleter.ExtractFirstObject("{\"unterminated\": 1"));
        Assert.Equal("{\"a\":{\"b\":1}}", StructuredCompleter.ExtractFirstObject("x {\"a\":{\"b\":1}} y {\"c\":2}"));
    }
}
