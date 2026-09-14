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
    public async Task A_saved_demographic_answer_goes_on_a_form_that_offers_it_and_is_never_flagged()
    {
        var gender = new PacketQuestion("gender", "Gender", false, PacketQuestion.Select,
            ["Male", "Female", "Decline To Self Identify"], PacketQuestion.Eeo);
        var stub = new StubLlmClient((_, _, _) => "{\"answers\":[]}");
        var drafter = new AnswerDrafter(new StructuredCompleter(stub));

        var (answers, review) = await drafter.DraftAsync([gender], Ctx(), Cfg,
            pinned: new Dictionary<string, string> { ["gender"] = "decline to self identify" });
        Assert.Equal("Decline To Self Identify", answers["gender"]);
        Assert.Empty(review);

        // Worded differently on this form: left blank, and still nothing for the person to fix.
        (answers, review) = await drafter.DraftAsync([gender], Ctx(), Cfg,
            pinned: new Dictionary<string, string> { ["gender"] = "I don't wish to answer" });
        Assert.False(answers.ContainsKey("gender"));
        Assert.Empty(review);

        // Nothing saved: never guessed, never flagged.
        (answers, review) = await drafter.DraftAsync([gender], Ctx(), Cfg);
        Assert.False(answers.ContainsKey("gender"));
        Assert.Empty(review);
    }

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

    [Theory]
    [InlineData("Omaha, NE", "United States")]
    [InlineData("Lincoln, Nebraska", "United States")]
    [InlineData("Lincoln, NE 68508", "United States")]
    [InlineData("Minden, Nebraska (Remote)", "United States")]
    [InlineData("Omaha, NE - Hybrid", "United States")]
    [InlineData("Berlin, Germany (Remote)", "Germany")]
    [InlineData("Austin, TX, USA", "United States")]
    [InlineData("Toronto, Canada", "Canada")]
    [InlineData("London, UK", "United Kingdom")]
    [InlineData("Berlin, Germany", "Germany")]
    [InlineData("Remote", "")]
    [InlineData("Omaha", "")]
    [InlineData("", "")]
    public void Country_is_read_off_the_resume_location_conservatively(string location, string expected) =>
        Assert.Equal(expected, AnswerDrafter.CountryFromLocation(location));

    [Fact]
    public void A_middle_initial_is_not_part_of_the_last_name()
    {
        // "Aaron K. Clark" went out as first "Aaron", last "K. Clark" (#200).
        Assert.Equal(("Aaron", "Clark"), AnswerDrafter.SplitName("Aaron K. Clark"));
        Assert.Equal(("Aaron", "Clark"), AnswerDrafter.SplitName("Aaron K Clark"));
        Assert.Equal(("Ada", "Byte"), AnswerDrafter.SplitName("Ada Byte"));
        Assert.Equal(("Mary", "Ann Smith"), AnswerDrafter.SplitName("Mary Ann Smith"));   // anyone's guess: the person pins it
        Assert.Equal(("Aaron", "Clark Jr."), AnswerDrafter.SplitName("Aaron K. Clark Jr."));
        Assert.Equal(("Cher", ""), AnswerDrafter.SplitName("Cher"));
        Assert.Equal(("Aaron", "K."), AnswerDrafter.SplitName("Aaron K."));               // nothing else to call a last name
        Assert.Equal(("", ""), AnswerDrafter.SplitName("  "));

        var last = new PacketQuestion("last_name", "Last Name", true, PacketQuestion.Text, [], PacketQuestion.Standard);
        var ctx = Ctx() with { Resume = new Resume { FullName = "Aaron K. Clark" } };
        Assert.Equal(("Clark", null), AnswerDrafter.Deterministic(last, ctx));
    }

    [Fact]
    public async Task A_pinned_name_beats_the_resume_on_every_form_whatever_the_field_is_called()
    {
        var first = new PacketQuestion("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard);
        var byId = new PacketQuestion("last_name", "Last Name", true, PacketQuestion.Text, [], PacketQuestion.Standard);
        var byLabel = new PacketQuestion("q_2", "Surname", true, PacketQuestion.Text, [], PacketQuestion.Custom);
        Assert.Equal("first_name", AnswerDrafter.NameField(first));
        Assert.Equal("last_name", AnswerDrafter.NameField(byId));
        Assert.Equal("last_name", AnswerDrafter.NameField(byLabel));
        Assert.Null(AnswerDrafter.NameField(new PacketQuestion("q_3", "Company name", true, PacketQuestion.Text, [], PacketQuestion.Custom)));
        // One key per name field, however the form labelled it.
        Assert.Equal(AnswerBankRepo.FirstNameKey, AnswerBankRepo.KeyFor(first));
        Assert.Equal(AnswerBankRepo.LastNameKey, AnswerBankRepo.KeyFor(byId));
        Assert.Equal(AnswerBankRepo.LastNameKey, AnswerBankRepo.KeyFor(byLabel));

        var stub = new StubLlmClient((_, _, _) => "{\"answers\":[]}");
        var pinned = new Dictionary<string, string> { [AnswerBankRepo.LastNameKey] = "Clark-Byte" };
        var (answers, review) = await new AnswerDrafter(new StructuredCompleter(stub))
            .DraftAsync([first, byId, byLabel], Ctx(), Cfg, pinned: pinned);
        Assert.Equal("Ada", answers["first_name"]);
        Assert.Equal("Clark-Byte", answers["last_name"]);
        Assert.Equal("Clark-Byte", answers["q_2"]);
        Assert.Empty(review);
        Assert.Equal(0, stub.Calls);

        // A built packet is brought up to date with the pinned name, not the résumé's split.
        var packet = new AgentPacket
        {
            ApplicationName = "acme.md", Questions = [first, byId],
            Answers = new Dictionary<string, string> { ["first_name"] = "Ada", ["last_name"] = "K. Byte" },
        };
        Assert.True(PacketBuilder.RefreshStandardAnswers(packet, Ctx(), pinned));
        Assert.Equal("Clark-Byte", packet.Answers["last_name"]);
        Assert.False(PacketBuilder.RefreshStandardAnswers(packet, Ctx(), pinned));
        // Without a pin the profile rules, as before.
        Assert.True(PacketBuilder.RefreshStandardAnswers(packet, Ctx()));
        Assert.Equal("Byte", packet.Answers["last_name"]);
    }

    [Fact]
    public void Country_and_city_are_deterministic_and_the_standing_country_wins()
    {
        // Greenhouse's synthetic country question, and a discovered form's "Country*" combobox.
        var byId = new PacketQuestion("country", "Country", false, PacketQuestion.Select, [], PacketQuestion.Standard);
        var byLabel = new PacketQuestion("q7", "Country of residence", true, PacketQuestion.Text, [], PacketQuestion.Custom);
        Assert.Equal(("United States", null), AnswerDrafter.Deterministic(byId, Ctx()));   // inferred from "Lincoln, NE"
        Assert.Equal(("United States", null), AnswerDrafter.Deterministic(byLabel, Ctx()));

        var ctx = Ctx() with { Settings = new AgentSettings { Country = "Canada" } };
        Assert.Equal(("Canada", null), AnswerDrafter.Deterministic(byId, ctx));

        var nowhere = Ctx() with { Resume = new Resume { FullName = "Ada Byte", Location = "Remote" } };
        var (answer, reason) = AnswerDrafter.Deterministic(byId, nowhere);
        Assert.Null(answer);
        Assert.Contains("country", reason);

        // The city autocomplete is labelled "Location (City)" on the form; a sponsorship
        // question that merely mentions "the posting location" is not a location question.
        var city = new PacketQuestion("candidate-location", "Location (City)", true, PacketQuestion.Text, [], PacketQuestion.Standard);
        Assert.Equal(("Lincoln, NE", null), AnswerDrafter.Deterministic(city, Ctx()));
        // The résumé's aside is for the reader; a geocoder chokes on it.
        var remote = Ctx() with { Resume = new Resume { FullName = "Ada Byte", Location = "Minden, Nebraska (Remote)" } };
        Assert.Equal(("Minden, Nebraska", null), AnswerDrafter.Deterministic(city, remote));
        var sponsorship = new PacketQuestion("q8", "Do you need sponsorship now or in the future to accept this job in the posting location?", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom);
        Assert.Equal(("No", null), AnswerDrafter.Deterministic(sponsorship, Ctx()));
        // GitLab's wording: a sponsorship question that ends in "your current location".
        var gitlab = new PacketQuestion("q9", "Will you now or in the future require sponsorship for a visa to remain in your current location?", true, PacketQuestion.Select, ["No", "Yes, EU Blue Card", "Yes, but not one of the visas listed here"], PacketQuestion.Custom);
        Assert.Equal(("No", null), AnswerDrafter.Deterministic(gitlab, Ctx()));
    }

    [Fact]
    public void A_country_pick_list_is_answered_with_its_own_spelling_and_a_combined_eligibility_question_goes_to_the_model()
    {
        var gitlab = new PacketQuestion("q1", "What is your current country of residence?", true, PacketQuestion.Select, ["Afghanistan", "United Kingdom", "United States of America"], PacketQuestion.Custom);
        Assert.Equal(("United States of America", null), AnswerDrafter.Deterministic(gitlab, Ctx()));
        var gr8 = new PacketQuestion("q2", "Choose your current location country", true, PacketQuestion.Select, ["Ukraine", "United States, U.S., USA"], PacketQuestion.Custom);
        Assert.Equal(("United States, U.S., USA", null), AnswerDrafter.Deterministic(gr8, Ctx()));
        var none = new PacketQuestion("q3", "Country of residence", true, PacketQuestion.Select, ["Mars", "Venus"], PacketQuestion.Custom);
        var (answer, reason) = AnswerDrafter.Deterministic(none, Ctx());
        Assert.Null(answer);
        Assert.Contains("pick the country option", reason);
        Assert.Equal("USA", AnswerDrafter.PickCountryOption(["Canada", "USA"], "United States"));
        Assert.Null(AnswerDrafter.PickCountryOption(["United Arab Emirates"], "United States"));

        // Miris: authorization and sponsorship in one question with fixed options — the
        // polarity of "No, I require support now" is not the polarity of the answer.
        var miris = new PacketQuestion("q4", "Are you legally authorized to work in the country where you intend to perform this role, and do you now or will you in the future require Miris to sponsor your work authorization?", true, PacketQuestion.Select, ["Yes, and I do not require sponsorship", "No, I require support now"], PacketQuestion.Custom);
        Assert.Equal((null, null), AnswerDrafter.Deterministic(miris, Ctx()));
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
    public async Task A_question_with_no_readable_label_is_never_put_to_the_model()
    {
        // Zoho's fields arrived labelled by their own opaque names, and the model guessed
        // "25+ years" for one and "Yes" for another — into boxes it never saw (#207).
        var questions = new List<PacketQuestion>
        {
            new("rec-form_50429000000003149", "rec-form_50429000000003149", true, PacketQuestion.Text, [], PacketQuestion.Custom),
            new("inputId", "inputId", false, PacketQuestion.Text, [], PacketQuestion.Custom),
            new("q1", "Why do you want to work here?", true, PacketQuestion.Textarea, [], PacketQuestion.Custom),
        };
        var asked = new List<string>();
        var stub = new StubLlmClient((_, user, _) =>
        {
            asked.Add(user);
            return "{\"answers\":[{\"id\":\"rec-form_50429000000003149\",\"answer\":\"25+ years\"},{\"id\":\"inputId\",\"answer\":\"Yes\"},{\"id\":\"q1\",\"answer\":\"Because billing.\"}]}";
        });

        var (answers, review) = await new AnswerDrafter(new StructuredCompleter(stub)).DraftAsync(questions, Ctx(), Cfg);

        Assert.Equal(["q1"], answers.Keys.ToArray());
        Assert.DoesNotContain("rec-form_50429000000003149", Assert.Single(asked));
        var item = Assert.Single(review);
        Assert.Equal("rec-form_50429000000003149", item.Id);   // required and unreadable: the human's
        Assert.Contains("no readable label", item.Reason);
    }

    [Theory]
    [InlineData("rec-form_50429000000003149", "rec-form_50429000000003149", true)]
    [InlineData("inputId", "inputId", true)]
    [InlineData("cards[abc][field0]", "cards[abc][field0]", true)]
    [InlineData("-add-skills", "-add-skills", true)]
    [InlineData("q", "", true)]
    [InlineData("country", "Country", false)]
    [InlineData("std:email", "email", false)]
    [InlineData("q7", "Why us?", false)]
    [InlineData("inputId", "State/Province", false)]
    public void An_opaque_label_is_the_controls_own_identifier_not_a_word(string id, string label, bool opaque) =>
        Assert.Equal(opaque, AnswerDrafter.IsOpaqueLabel(new(id, label, false, PacketQuestion.Text, [], PacketQuestion.Custom)));

    [Fact]
    public void A_social_profile_box_takes_that_sites_link_or_stays_empty()
    {
        var ctx = Ctx() with { Resume = new Resume { Links = [new ResumeLink("Facebook", "https://facebook.com/ada")] } };
        var (fb, _) = AnswerDrafter.Deterministic(new("q", "Facebook", false, PacketQuestion.Text, [], PacketQuestion.Custom), ctx);
        Assert.Equal("https://facebook.com/ada", fb);

        // No such link: a reason, never free text — and optional, so it will not block.
        var (x, why) = AnswerDrafter.Deterministic(new("q", "X (formerly Twitter)", false, PacketQuestion.Text, [], PacketQuestion.Custom), ctx);
        Assert.Null(x);
        Assert.Contains("link in your résumé", why);
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

    [Fact]
    public void Only_required_questions_block_a_submission()
    {
        var packet = new AgentPacket
        {
            Questions =
            [
                new("required_q", "Why this role?", true, PacketQuestion.Text, [], PacketQuestion.Custom),
                new("optional_q", "Anything else?", false, PacketQuestion.Textarea, [], PacketQuestion.Custom),
            ],
            NeedsReview =
            [
                new("optional_q", "the model could not answer this from your résumé"),
            ],
        };

        // An optional question the model declined is on the review list for the human to
        // see, but it must not park the application — the form itself does not want it.
        Assert.Empty(packet.BlockingReview());

        packet.NeedsReview.Add(new("required_q", "required, and no answer yet"));
        Assert.Equal(["required_q"], packet.BlockingReview().Select(r => r.Id).ToArray());
    }

    [Fact]
    public void A_review_item_whose_question_vanished_still_blocks()
    {
        var packet = new AgentPacket
        {
            Questions = [new("kept", "Kept", false, PacketQuestion.Text, [], PacketQuestion.Custom)],
            NeedsReview = [new("gone", "asked on a form we no longer have")],
        };

        Assert.Equal(["gone"], packet.BlockingReview().Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_pinned_answer_from_the_bank_wins_over_the_model_and_must_name_an_option_on_a_fixed_list()
    {
        var questions = GreenhouseBoard.Parse(GreenhouseBoardTests.JobJson).Questions;
        var stub = new StubLlmClient((_, _, _) => "{\"answers\":[{\"id\":\"question_3\",\"answer\":\"MODEL\"}]}");
        var pinned = new Dictionary<string, string>
        {
            [AnswerBankRepo.KeyFor("Describe a system you scaled.")] = "I scaled the billing service to 10x, by hand.",
            [AnswerBankRepo.KeyFor("Are you legally authorized to work in the United States?")] = "yes",
        };

        var (answers, review) = await new AnswerDrafter(new StructuredCompleter(stub))
            .DraftAsync(questions, Ctx(), Cfg, pinned: pinned);

        Assert.Equal("I scaled the billing service to 10x, by hand.", answers["question_3"]);
        Assert.Equal("Yes", answers["question_2"]);            // the option's own spelling
        Assert.Equal(0, stub.Calls);                            // nothing was left for the model
        Assert.DoesNotContain(review, r => r.Id is "question_2" or "question_3");

        // A saved answer that is not on the list is flagged, never typed.
        pinned[AnswerBankRepo.KeyFor("Are you legally authorized to work in the United States?")] = "Sure";
        var (again, flagged) = await new AnswerDrafter(new StructuredCompleter(stub)).DraftAsync(questions, Ctx(), Cfg, pinned: pinned);
        Assert.False(again.ContainsKey("question_2"));
        Assert.Contains(flagged, r => r.Id == "question_2" && r.Reason.Contains("isn't one of this form's options"));
    }

    [Fact]
    public void Fit_to_options_matches_case_insensitively_and_splits_a_multiselect()
    {
        var single = new PacketQuestion("q", "Pick", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom);
        Assert.Equal(("Yes", null), AnswerDrafter.FitToOptions(single, " yes "));
        Assert.Null(AnswerDrafter.FitToOptions(single, "maybe").Answer);
        var multi = new PacketQuestion("q", "Stack", true, PacketQuestion.MultiSelect, ["C#", "Python", "Go"], PacketQuestion.Custom);
        Assert.Equal(("C#, Python", null), AnswerDrafter.FitToOptions(multi, "c#, python"));
        Assert.Null(AnswerDrafter.FitToOptions(multi, "C#, Rust").Answer);
        var free = new PacketQuestion("q", "Why", true, PacketQuestion.Textarea, [], PacketQuestion.Custom);
        Assert.Equal(("because", null), AnswerDrafter.FitToOptions(free, "because"));
    }
}
