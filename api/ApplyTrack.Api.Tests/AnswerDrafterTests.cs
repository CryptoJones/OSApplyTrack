// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Greenhouse;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Deterministic answers come from the tenant's facts and never reach the model; the
/// model only sees the screening questions; anything unanswerable is flagged rather
/// than invented; EEO is never touched.
/// </summary>
public class AnswerDrafterTests
{
    private static readonly EffectiveLlmConfig Cfg = new("http://local/v1", "test-model", null, 30);

    private static AnswerContext Ctx(string letter = "Dear team, hello.") => new(
        new Resume
        {
            FullName = "Ada Byte", Summary = "Ships .NET.", Location = "Lincoln, NE",
            Links = [new ResumeLink("LinkedIn", "https://linkedin.com/in/ada"), new ResumeLink("GitHub", "https://github.com/ada")],
        },
        new AgentSettings { WorkAuthorization = "US citizen", Phone = "555-0100", SalaryExpectation = "$150k" },
        "ada@example.com", letter, "We need a .NET engineer.");

    [Fact]
    public async Task Greenhouse_form_gets_deterministic_answers_and_one_model_call_for_the_rest()
    {
        var questions = GreenhouseBoard.Parse(GreenhouseBoardTests.JobJson).Questions;
        var stub = new StubLlmClient((_, _, _) =>
            "{\"answers\":[{\"id\":\"question_3\",\"answer\":\"I scaled the billing service to 10x.\"},{\"id\":\"first_name\",\"answer\":\"HACK\"}]}");

        var (answers, review) = await new AnswerDrafter(new StructuredCompleter(stub)).DraftAsync(questions, Ctx(), Cfg);

        Assert.Equal("Ada", answers["first_name"]);
        Assert.Equal("Byte", answers["last_name"]);
        Assert.Equal("ada@example.com", answers["email"]);
        Assert.Equal("555-0100", answers["phone"]);
        Assert.Equal("https://linkedin.com/in/ada", answers["question_1"]);
        Assert.Equal("Yes", answers["question_2"]);            // authorization, by option, no model
        Assert.Equal("I scaled the billing service to 10x.", answers["question_3"]);
        Assert.False(answers.ContainsKey("gender"));           // EEO never answered
        Assert.False(answers.ContainsKey("resume"));           // files are attached at submit
        Assert.Empty(review);

        Assert.Equal(1, stub.Calls);
        Assert.Contains("question_3", stub.LastUserPrompt);
        Assert.DoesNotContain("first_name", stub.LastUserPrompt);   // deterministic ones never go to the model
        Assert.DoesNotContain("Gender", stub.LastUserPrompt);
    }

    [Fact]
    public async Task What_the_model_cannot_answer_is_flagged_not_invented()
    {
        var questions = new List<PacketQuestion>
        {
            new("q1", "Why do you want to work here?", true, PacketQuestion.Textarea, [], PacketQuestion.Custom),
            new("q2", "Favourite colour?", false, PacketQuestion.Text, [], PacketQuestion.Custom),
            new("q3", "Preferred office", true, PacketQuestion.Select, ["Lincoln", "Omaha"], PacketQuestion.Custom),
        };
        var stub = new StubLlmClient((_, _, _) =>
            "{\"answers\":[{\"id\":\"q1\",\"answer\":null},{\"id\":\"q2\",\"answer\":null},{\"id\":\"q3\",\"answer\":\"omaha please\"}]}");

        var (answers, review) = await new AnswerDrafter(new StructuredCompleter(stub)).DraftAsync(questions, Ctx(), Cfg);

        Assert.Empty(answers);   // a paraphrased option does not count
        Assert.Equal(["q1", "q2", "q3"], review.Select(r => r.Id).OrderBy(x => x).ToArray());
        Assert.All(review, r => Assert.Contains("model", r.Reason));
    }

    [Fact]
    public async Task A_dead_model_still_yields_a_packet_with_everything_owed_flagged()
    {
        var questions = new List<PacketQuestion>
        {
            new("std:first_name", "First name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("q1", "Tell us about yourself", true, PacketQuestion.Textarea, [], PacketQuestion.Custom),
        };
        var stub = new StubLlmClient((_, _, _) => "no");

        var (answers, review) = await new AnswerDrafter(new StructuredCompleter(stub)).DraftAsync(questions, Ctx(), Cfg);

        Assert.Equal("Ada", answers["std:first_name"]);
        var item = Assert.Single(review);
        Assert.Equal("q1", item.Id);
        Assert.StartsWith("no draft", item.Reason);
    }

    [Fact]
    public void Human_only_and_missing_facts_are_flagged_with_a_pointer()
    {
        var ctx = new AnswerContext(new Resume(), new AgentSettings(), "", "", "");
        var (a1, r1) = AnswerDrafter.Deterministic(
            new("q", "How did you hear about us?", true, PacketQuestion.Text, [], PacketQuestion.Custom), ctx);
        Assert.Null(a1);
        Assert.Contains("only you", r1);

        var (a2, r2) = AnswerDrafter.Deterministic(
            new("phone", "Phone", true, PacketQuestion.Text, [], PacketQuestion.Standard), ctx);
        Assert.Null(a2);
        Assert.Contains("Settings · Agent", r2);

        var (a3, r3) = AnswerDrafter.Deterministic(
            new("q", "Will you require sponsorship?", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom), ctx);
        Assert.Equal("No", a3);
        Assert.Null(r3);

        var (a4, r4) = AnswerDrafter.Deterministic(
            new("q", "Desired salary", true, PacketQuestion.Text, [], PacketQuestion.Custom), ctx);
        Assert.Null(a4);
        Assert.Contains("salary", r4);
    }

    // -- money units (issue #120) ---------------------------------------------
    //
    // A saved salary is a bare number carrying an assumed period and currency. When a
    // field states its own and they differ, guessing produces a well-formed wrong answer
    // that passes every downstream check and gets submitted. Observed live: an annual USD
    // expectation typed into "remuneration (Gross) per month, in EUR".

    [Fact]
    public void A_salary_field_that_states_different_units_goes_to_review_not_a_guess()
    {
        var ctx = Ctx();   // SalaryExpectation = "$150k" -> annual USD by the $ sign
        var q = new PacketQuestion("q", "Enter your remuneration expectations",
            false, PacketQuestion.Text, [], PacketQuestion.Custom)
        {
            Help = "Please, provide remuneration (Gross) per month, in EUR for BtoB cooperation",
        };

        var (answer, reason) = AnswerDrafter.Deterministic(q, ctx);

        Assert.Null(answer);
        Assert.Contains("monthly EUR", reason);
    }

    [Fact]
    public void A_saved_figure_that_does_not_state_its_period_is_not_assumed_to_match()
    {
        // "$150k" names a currency but no period. Convention says annual; the code does
        // not get to rely on convention when the field is explicit and the answer is not.
        var q = new PacketQuestion("q", "Desired salary", false, PacketQuestion.Text, [], PacketQuestion.Custom)
        {
            Help = "Annual, in USD",
        };

        var (answer, reason) = AnswerDrafter.Deterministic(q, Ctx());

        Assert.Null(answer);
        // The reason names both sides so the user can see exactly what did not line up.
        Assert.Contains("wants annual USD", reason);
        Assert.Contains("is USD", reason);
    }

    [Fact]
    public void A_salary_field_whose_units_match_the_saved_figure_still_answers()
    {
        var ctx = new AnswerContext(
            new Resume { FullName = "Ada Byte" },
            new AgentSettings { SalaryExpectation = "150,000 USD per year" },
            "ada@example.com", "", "");
        var q = new PacketQuestion("q", "Desired salary", false, PacketQuestion.Text, [], PacketQuestion.Custom)
        {
            Help = "Annual, in USD",
        };

        var (answer, reason) = AnswerDrafter.Deterministic(q, ctx);

        Assert.Equal("150,000 USD per year", answer);
        Assert.Null(reason);
    }

    [Fact]
    public void A_salary_field_that_states_no_units_is_answered_as_before()
    {
        // No stated units means no evidence of a mismatch — do not invent friction.
        var (answer, reason) = AnswerDrafter.Deterministic(
            new PacketQuestion("q", "Desired salary", false, PacketQuestion.Text, [], PacketQuestion.Custom),
            Ctx());

        Assert.Equal("$150k", answer);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("provide remuneration per month, in EUR", "monthly EUR")]
    [InlineData("$150k", "USD")]
    [InlineData("150000 per year USD", "annual USD")]
    [InlineData("£60 per hour", "hourly GBP")]
    [InlineData("150000", "")]
    public void Money_units_reports_only_what_the_text_actually_says(string text, string expected) =>
        Assert.Equal(expected, AnswerDrafter.MoneyUnits(text));

    [Fact]
    public void Help_text_joins_the_label_for_matching()
    {
        var bare = new PacketQuestion("q", "Desired salary", false, PacketQuestion.Text, [], PacketQuestion.Custom);
        Assert.Equal("Desired salary", bare.FullPrompt);
        Assert.Equal("", bare.Help);   // defaulted, so packets stored before 1.22 still load

        var hinted = bare with { };
        hinted = new PacketQuestion("q", "Desired salary", false, PacketQuestion.Text, [], PacketQuestion.Custom)
        {
            Help = "per month",
        };
        Assert.Equal("Desired salary (per month)", hinted.FullPrompt);
    }

    [Fact]
    public void Recompute_review_blocks_on_required_blanks_and_clears_answered_ones()
    {
        var packet = new AgentPacket
        {
            Questions =
            [
                new("a", "A", true, PacketQuestion.Text, [], PacketQuestion.Custom),
                new("b", "B", false, PacketQuestion.Text, [], PacketQuestion.Custom),
                new("resume", "Résumé", true, PacketQuestion.File, [], PacketQuestion.Standard),
                new("gender", "Gender", false, PacketQuestion.Select, ["X"], PacketQuestion.Eeo),
            ],
            Answers = new() { ["a"] = "", ["b"] = "" },
            NeedsReview = [new("b", "the model could not answer this")],
        };
        packet.RecomputeReview();
        Assert.Equal(["a", "b"], packet.NeedsReview.Select(r => r.Id).ToArray());

        packet.Answers["a"] = "done";
        packet.Answers["b"] = "also";
        packet.RecomputeReview();
        Assert.Empty(packet.NeedsReview);
    }
}
