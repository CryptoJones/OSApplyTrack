// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The salary expectation carries a period and a currency (#188): a form asking for
/// another period in the same currency gets the figure converted; one asking in another
/// currency, or one the saved figure cannot be converted for, is still the person's.
/// </summary>
public class SalaryConversionTests
{
    private static AgentSettings Annual(string figure = "125000", string currency = "USD") =>
        new() { SalaryExpectation = figure, SalaryPeriod = "annual", SalaryCurrency = currency };

    [Theory]
    [InlineData("Expected monthly salary (USD)", "10417")]
    [InlineData("Hourly rate expectation, in USD", "60.1")]
    [InlineData("Expected salary per year (USD)", "125000")]
    [InlineData("Salary expectation", "125000")]
    public void An_annual_figure_is_converted_to_the_period_the_form_asks_for(string prompt, string expected)
    {
        var (answer, reason) = AnswerDrafter.SalaryFor(prompt, Annual());
        Assert.Null(reason);
        Assert.Equal(expected, answer);
    }

    [Fact]
    public void A_monthly_or_hourly_figure_converts_the_other_way()
    {
        Assert.Equal("120000", AnswerDrafter.SalaryFor("Annual salary expectation (USD)",
            new AgentSettings { SalaryExpectation = "10,000", SalaryPeriod = "monthly", SalaryCurrency = "USD" }).Answer);
        Assert.Equal("104000", AnswerDrafter.SalaryFor("Expected yearly compensation in USD",
            new AgentSettings { SalaryExpectation = "$50", SalaryPeriod = "hourly", SalaryCurrency = "USD" }).Answer);
        Assert.Equal("8667", AnswerDrafter.SalaryFor("Expected monthly salary (USD)",
            new AgentSettings { SalaryExpectation = "50/hr", SalaryPeriod = "hourly", SalaryCurrency = "USD" }).Answer);
    }

    [Fact]
    public void Another_currency_is_never_converted()
    {
        var (answer, reason) = AnswerDrafter.SalaryFor("Expected monthly salary (EUR)", Annual());
        Assert.Null(answer);
        Assert.Contains("wants monthly EUR", reason);
        Assert.Contains("is annual USD", reason);
    }

    [Fact]
    public void A_figure_with_no_stated_period_is_not_converted()
    {
        var (answer, reason) = AnswerDrafter.SalaryFor("Expected monthly salary (USD)",
            new AgentSettings { SalaryExpectation = "125000", SalaryCurrency = "USD" });
        Assert.Null(answer);
        Assert.Contains("no period stated", reason);
        Assert.Contains("Settings · Agent", reason);
    }

    [Fact]
    public void A_range_or_prose_cannot_be_converted_and_is_handed_over()
    {
        var (answer, reason) = AnswerDrafter.SalaryFor("Expected monthly salary (USD)", Annual("120-140k"));
        Assert.Null(answer);
        Assert.Contains("not a plain number", reason);
    }

    [Fact]
    public void The_stated_units_win_over_what_the_figure_says()
    {
        // The setting says monthly EUR even though the figure itself names nothing.
        var s = new AgentSettings { SalaryExpectation = "8000", SalaryPeriod = "monthly", SalaryCurrency = "EUR" };
        Assert.Equal("96000", AnswerDrafter.SalaryFor("Gross annual salary expectation (EUR)", s).Answer);
        Assert.Null(AnswerDrafter.SalaryFor("Gross annual salary expectation (USD)", s).Answer);
    }

    [Theory]
    [InlineData("$140,000", "140000")]
    [InlineData("125k", "125000")]
    [InlineData("60.50/hr", "60.50")]
    [InlineData("150000 USD per year", "150000")]
    [InlineData("120-140k", null)]
    [InlineData("negotiable", null)]
    public void Amounts_are_read_from_the_usual_spellings(string text, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            AnswerDrafter.ParseAmount(text));

    [Fact]
    public void The_drafter_uses_the_conversion_and_the_eligibility_brief_states_the_units()
    {
        var ctx = new AnswerContext(new Resume { FullName = "Ada Byte" }, Annual(), "ada@example.com", "", "");
        var q = new PacketQuestion("q", "Expected monthly salary (USD)", true, PacketQuestion.Text, [], PacketQuestion.Custom);
        Assert.Equal(("10417", null), AnswerDrafter.Deterministic(q, ctx));
        Assert.Contains("Salary expectation: 125000 (annual, USD)", Annual().ToEligibilityBrief());
    }

    [Fact]
    public void Settings_normalise_the_period_and_currency()
    {
        var s = AgentSettings.FromJson(System.Text.Json.JsonDocument.Parse(
            """{"salary_expectation":"125000","salary_period":"Monthly","salary_currency":"usd"}""").RootElement);
        Assert.Equal(("monthly", "USD"), (s.SalaryPeriod, s.SalaryCurrency));
        var junk = AgentSettings.FromJson(System.Text.Json.JsonDocument.Parse(
            """{"salary_period":"fortnightly","salary_currency":"dollars-and-cents"}""").RootElement);
        Assert.Equal(("", "DOLLARS-"), (junk.SalaryPeriod, junk.SalaryCurrency));
    }
}
