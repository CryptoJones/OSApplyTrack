// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Unit tests for the prompt-building / guard logic in <see cref="CoverLetterDrafter"/>,
/// driven by a deterministic <see cref="StubLlmClient"/> so no model is hit. They pin
/// the rules the endpoint relies on: refuse an empty résumé before spending a call,
/// feed the model only the tenant's own facts, and reject an implausible reply.
/// </summary>
public class CoverLetterDrafterTests
{
    private static readonly EffectiveLlmConfig Cfg = new("http://local/v1", "test-model", null, 30);

    private static Resume SampleResume() => new()
    {
        FullName = "Ada Byte",
        Headline = "Backend Engineer",
        Summary = "Ships reliable services.",
        Skills = ["C#", "Postgres"],
    };

    [Fact]
    public async Task An_empty_resume_is_rejected_before_the_model_is_called()
    {
        var stub = new StubLlmClient();
        var drafter = new CoverLetterDrafter(stub);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            drafter.DraftAsync(new AppFields { Company = "Acme" }, Resume.Empty(), Cfg));
        Assert.Equal(0, stub.Calls); // never spent an LLM call
    }

    [Fact]
    public async Task The_prompt_carries_the_app_and_resume_facts_and_the_lane_lead()
    {
        var stub = new StubLlmClient();
        var drafter = new CoverLetterDrafter(stub);

        var body = await drafter.DraftAsync(
            new AppFields { Company = "Acme Corp", Role = "Engineer", Lane = "dotnet" }, SampleResume(), Cfg);

        Assert.Equal(StubLlmClient.DefaultBody, body);
        Assert.Contains("Acme Corp", stub.LastUserPrompt);
        Assert.Contains("Ada Byte", stub.LastUserPrompt);   // the only facts the model may assert
        Assert.Contains(".NET", stub.LastSystemPrompt);      // dotnet lane lead steered the system prompt
    }

    [Fact]
    public async Task The_returned_body_is_trimmed()
    {
        var stub = new StubLlmClient((_, _, _) => "   " + new string('y', 100) + "   ");
        var body = await new CoverLetterDrafter(stub)
            .DraftAsync(new AppFields { Company = "Acme" }, SampleResume(), Cfg);
        Assert.Equal(new string('y', 100), body);
    }

    [Fact]
    public async Task A_saved_signature_is_appended_exactly()
    {
        var body = await new CoverLetterDrafter(new StubLlmClient())
            .DraftAsync(new AppFields { Company = "Acme" }, SampleResume(), Cfg, "Sincerely,\nAda Byte");
        Assert.EndsWith("Sincerely,\nAda Byte", body);
    }

    [Theory]
    [InlineData("too short")]                  // under the 40-char floor
    [InlineData("")]                            // empty
    public async Task An_implausible_reply_is_treated_as_unusable(string reply)
    {
        var stub = new StubLlmClient((_, _, _) => reply);
        await Assert.ThrowsAsync<LlmUnavailableException>(() => new CoverLetterDrafter(stub)
            .DraftAsync(new AppFields { Company = "Acme" }, SampleResume(), Cfg));
    }

    [Fact]
    public async Task The_fetched_posting_reaches_the_prompt()
    {
        var stub = new StubLlmClient();

        await new CoverLetterDrafter(stub).DraftAsync(
            new AppFields { Company = "Acme", Link = "https://acme.example/jobs/1" },
            SampleResume(), Cfg, "",
            "We need someone fluent in Npgsql and Dapper to own our ingest pipeline.");

        Assert.Contains("Npgsql and Dapper", stub.LastUserPrompt);
        Assert.Contains("own our ingest pipeline", stub.LastUserPrompt);
    }

    [Fact]
    public async Task Without_posting_text_the_prompt_says_so_rather_than_implying_it_was_read()
    {
        var stub = new StubLlmClient();

        await new CoverLetterDrafter(stub).DraftAsync(
            new AppFields { Company = "Acme", Link = "https://acme.example/jobs/1" },
            SampleResume(), Cfg);

        // The URL still goes through — the model just must not be led to believe it
        // has seen the description when all it has is a link it cannot open.
        Assert.Contains("https://acme.example/jobs/1", stub.LastUserPrompt);
        Assert.Contains("not available", stub.LastUserPrompt);
    }

    [Fact]
    public async Task A_very_long_posting_is_truncated_so_it_cannot_crowd_out_the_resume()
    {
        var stub = new StubLlmClient();
        // Boilerplate first, the one fact that must survive the cut last.
        var posting = new string('x', 12_000) + "SENTINEL-TAIL";

        await new CoverLetterDrafter(stub).DraftAsync(
            new AppFields { Company = "Acme" }, SampleResume(), Cfg, "", posting);

        Assert.Contains("[…posting truncated]", stub.LastUserPrompt);
        Assert.DoesNotContain("SENTINEL-TAIL", stub.LastUserPrompt);
        Assert.Contains("Ada Byte", stub.LastUserPrompt);       // résumé survived the cap
    }
}
