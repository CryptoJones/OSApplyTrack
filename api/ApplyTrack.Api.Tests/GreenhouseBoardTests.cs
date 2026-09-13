// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Greenhouse;
using ApplyTrack.Api.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

/// <summary>Greenhouse's public job document becomes a typed form; provider detection
/// trusts the poller's source stamp over the URL.</summary>
public class GreenhouseBoardTests
{
    // A trimmed real-shaped document: standard fields, a résumé with file + text twins,
    // two screening questions (one select), and an EEO block.
    public const string JobJson = """
        {
         "id": 4001,
         "title": "Senior .NET Engineer",
         "content": "<p>We build <b>things</b>.</p>",
         "absolute_url": "https://job-boards.greenhouse.io/acme/jobs/4001",
         "questions": [
          {
           "required": true,
           "label": "First Name",
           "fields": [
            {
             "name": "first_name",
             "type": "input_text",
             "values": []
            }
           ]
          },
          {
           "required": true,
           "label": "Last Name",
           "fields": [
            {
             "name": "last_name",
             "type": "input_text",
             "values": []
            }
           ]
          },
          {
           "required": true,
           "label": "Email",
           "fields": [
            {
             "name": "email",
             "type": "input_text",
             "values": []
            }
           ]
          },
          {
           "required": false,
           "label": "Phone",
           "fields": [
            {
             "name": "phone",
             "type": "input_text",
             "values": []
            }
           ]
          },
          {
           "required": true,
           "label": "Resume/CV",
           "fields": [
            {
             "name": "resume_text",
             "type": "textarea",
             "values": []
            },
            {
             "name": "resume",
             "type": "input_file",
             "values": []
            }
           ]
          },
          {
           "required": false,
           "label": "LinkedIn Profile",
           "fields": [
            {
             "name": "question_1",
             "type": "input_text",
             "values": []
            }
           ]
          },
          {
           "required": true,
           "label": "Are you legally authorized to work in the United States?",
           "fields": [
            {
             "name": "question_2",
             "type": "multi_value_single_select",
             "values": [
              {
               "label": "Yes",
               "value": 0
              },
              {
               "label": "No",
               "value": 1
              }
             ]
            }
           ]
          },
          {
           "required": true,
           "label": "Describe a system you scaled.",
           "fields": [
            {
             "name": "question_3",
             "type": "textarea",
             "values": []
            }
           ]
          },
          {
           "required": false,
           "label": "tracker",
           "fields": [
            {
             "name": "hidden",
             "type": "input_hidden",
             "values": []
            }
           ]
          }
         ],
         "compliance": [
          {
           "type": "eeoc",
           "questions": [
            {
             "required": false,
             "label": "Gender",
             "fields": [
              {
               "name": "gender",
               "type": "multi_value_single_select",
               "values": [
                {
                 "label": "Male",
                 "value": 1
                },
                {
                 "label": "Female",
                 "value": 2
                },
                {
                 "label": "Decline To Self Identify",
                 "value": 3
                }
               ]
              }
             ]
            }
           ]
          }
         ]
        }
        """;

    [Fact]
    public void A_job_with_location_questions_gets_the_country_picker_its_form_renders_but_its_api_omits()
    {
        var with = GreenhouseBoard.Parse("""
            {"title":"x","content":"","questions":[{"required":true,"label":"First Name","fields":[{"name":"first_name","type":"input_text","values":[]}]}],
             "location_questions":[{"required":true,"label":"Longitude","fields":[{"name":"longitude","type":"input_hidden"}]},
                                   {"required":true,"label":"Location","fields":[{"name":"location","type":"input_text","values":[]}]}]}
            """);
        var country = Assert.Single(with.Questions, q => q.Id == "country");
        Assert.False(country.Required);   // whether THIS form renders one is decided in the browser
        Assert.Equal(PacketQuestion.Standard, country.Kind);
        Assert.Equal(["first_name", "country", "location"], with.Questions.Select(q => q.Id));

        var without = GreenhouseBoard.Parse("""{"title":"x","content":"","questions":[{"required":true,"label":"First Name","fields":[{"name":"first_name","type":"input_text","values":[]}]}]}""");
        Assert.DoesNotContain(without.Questions, q => q.Id == "country");
    }

    [Fact]
    public void Parses_questions_types_options_and_marks_eeo()
    {
        var job = GreenhouseBoard.Parse(JobJson);

        Assert.Equal("Senior .NET Engineer", job.Title);
        var byId = job.Questions.ToDictionary(q => q.Id);
        Assert.Equal(PacketQuestion.Standard, byId["first_name"].Kind);
        Assert.True(byId["first_name"].Required);
        // The résumé pair collapses to the file field.
        Assert.Equal(PacketQuestion.File, byId["resume"].Type);
        Assert.False(byId.ContainsKey("resume_text"));
        Assert.Equal(PacketQuestion.Select, byId["question_2"].Type);
        Assert.Equal(["Yes", "No"], byId["question_2"].Options);
        Assert.Equal(PacketQuestion.Custom, byId["question_3"].Kind);
        Assert.Equal(PacketQuestion.Textarea, byId["question_3"].Type);
        Assert.False(byId.ContainsKey("hidden"));
        Assert.Equal(PacketQuestion.Eeo, byId["gender"].Kind);
        Assert.False(byId["gender"].Required);
    }

    [Fact]
    public async Task Fetches_the_job_with_questions_and_returns_null_when_unknown()
    {
        var handler = CapturingHandler.Always(HttpStatusCode.OK, JobJson);
        var board = new GreenhouseBoard(
            new StubHttpClientFactory(handler, new Uri("https://boards-api.greenhouse.io/")),
            NullLogger<GreenhouseBoard>.Instance);

        var job = await board.GetJobAsync("acme", "4001");
        Assert.NotNull(job);
        Assert.Equal("/v1/boards/acme/jobs/4001", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("?questions=true", handler.Requests[0].Request.RequestUri!.Query);

        var missing = new GreenhouseBoard(
            new StubHttpClientFactory(CapturingHandler.Always(HttpStatusCode.NotFound, "{}"), new Uri("https://boards-api.greenhouse.io/")),
            NullLogger<GreenhouseBoard>.Instance);
        Assert.Null(await missing.GetJobAsync("acme", "4001"));
        Assert.Null(await board.GetJobAsync("../evil", "4001"));
        Assert.Null(await board.GetJobAsync("acme", "not-a-number"));
    }

    [Theory]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/4001", "", "greenhouse", "acme", "4001")]
    [InlineData("https://boards.greenhouse.io/acme/jobs/4001?t=abc", "", "greenhouse", "acme", "4001")]
    [InlineData("https://careers.acme.com/jobs?gh_jid=4001", "auto:greenhouse:acme", "greenhouse", "acme", "4001")]
    [InlineData("https://careers.acme.com/jobs?gh_jid=4001", "", "greenhouse", "", "")]
    public void Detects_greenhouse_and_parses_the_board_and_job(string link, string source, string provider, string board, string job)
    {
        Assert.Equal(provider, AtsProvider.Detect(link, source));
        var ok = AtsProvider.TryParseGreenhouse(link, source, out var b, out var j);
        Assert.Equal(board.Length > 0, ok);
        Assert.Equal(board, b);
        Assert.Equal(job, j);
    }

    [Theory]
    [InlineData("https://jobs.lever.co/acme/123", "", "lever")]
    [InlineData("https://jobs.ashbyhq.com/acme/123", "", "ashby")]
    [InlineData("https://acme.wd5.myworkdayjobs.com/en-US/careers/job/123", "", "workday")]
    [InlineData("https://example.com/jobs/1", "auto:lever:acme", "lever")]
    [InlineData("https://example.com/jobs/1", "auto:remotive", "unknown")]
    [InlineData("", "", "unknown")]
    public void Detects_the_other_providers_from_source_then_host(string link, string source, string provider) =>
        Assert.Equal(provider, AtsProvider.Detect(link, source));
}

public class AtsRoutingTests
{
    [Theory]
    [InlineData("https://jobs.lever.co/acme/1234-abcd", "lever", "https://jobs.lever.co/acme/1234-abcd/apply")]
    [InlineData("https://jobs.lever.co/acme/1234-abcd/apply", "lever", "https://jobs.lever.co/acme/1234-abcd/apply")]
    [InlineData("https://jobs.ashbyhq.com/acme/9f8e", "ashby", "https://jobs.ashbyhq.com/acme/9f8e/application")]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/1", "greenhouse", "https://job-boards.greenhouse.io/acme/jobs/1")]
    [InlineData("https://careers.acme.com/x", "unknown", "https://careers.acme.com/x")]
    public void The_form_is_one_hop_past_a_lever_or_ashby_posting(string link, string provider, string expected) =>
        Assert.Equal(expected, ApplyTrack.Api.Agent.AtsProvider.ApplyUrl(link, provider));

    [Theory]
    [InlineData("greenhouse", false, true)]
    [InlineData("lever", false, true)]
    [InlineData("ashby", false, true)]
    [InlineData("workday", true, false)]
    [InlineData("unknown", false, false)]
    [InlineData("unknown", true, true)]
    public void The_browser_drives_known_ats_the_long_tail_only_opted_in_and_workday_never(string provider, bool optIn, bool expected) =>
        Assert.Equal(expected, ApplyTrack.Api.Agent.AtsProvider.BrowserCanSubmit(provider, optIn));
}
