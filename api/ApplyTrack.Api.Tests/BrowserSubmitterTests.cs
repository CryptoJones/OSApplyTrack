// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
using System.Net;
using System.Text;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Drives the real submitter through a real Playwright server (the Node package the
/// web tests already install, started with <c>run-server</c>) at a fixture ATS form
/// served on loopback. Asserts on the POST the fixture receives — a dry run must
/// produce none. Skipped when the Node Playwright is not installed. <b>Every target
/// here resolves to loopback</b> and the fixture asserts it, so a stray real URL
/// fails loudly instead of applying to someone's job.
/// </summary>
public sealed class BrowserSubmitterTests : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Cli = Path.Combine(RepoRoot, "node_modules", "playwright-core", "cli.js");
    public static bool Available => File.Exists(Cli);

    private Process? _server;
    private string _ws = "";
    private WebApplication? _fixture;
    private string _fixtureUrl = "";
    private readonly List<Dictionary<string, string>> _posts = [];

    public async Task InitializeAsync()
    {
        if (!Available) return;
        _server = Process.Start(new ProcessStartInfo("node", $"\"{Cli}\" run-server --port 0 --host 127.0.0.1")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = RepoRoot,
        })!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var line = await _server.StandardOutput.ReadLineAsync();
            if (line is null) break;
            var i = line.IndexOf("ws://", StringComparison.Ordinal);
            if (i >= 0) { _ws = line[i..].Trim(); break; }
        }
        Assert.NotEqual("", _ws);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _fixture = builder.Build();
        _fixture.MapGet("/jobs/1", () => Results.Content(FormHtml, "text/html"));
        // A board whose own uploader rejects the files it was handed — Greenhouse's live
        // failure, reproduced: the input accepts them, the uploader then errors, nothing uploads.
        _fixture.MapGet("/jobs/uploader", () => Results.Content(BrokenUploaderHtml, "text/html"));
        // Greenhouse's shape: the uploader rejects the file, but the form offers to take the
        // résumé as text instead.
        _fixture.MapGet("/jobs/manual", () => Results.Content(ManualResumeHtml, "text/html"));
        // A challenge that only appears once Submit is clicked.
        _fixture.MapGet("/jobs/captcha-on-submit", () => Results.Content(CaptchaOnSubmitHtml, "text/html"));
        // A form guarded by an interactive captcha.
        _fixture.MapGet("/jobs/captcha", () => Results.Content(CaptchaFormHtml, "text/html"));
        // The invisible reCAPTCHA v3 badge, which must NOT count as a captcha.
        _fixture.MapGet("/jobs/badge", () => Results.Content(BadgeFormHtml, "text/html"));
        // A posting that closed between discovery and now: the board's gone-notice, no form.
        _fixture.MapGet("/jobs/closed", () => Results.Content(
            "<html><body><h1>Current openings at Acme</h1>"
            + "<p>The job you are looking for is no longer open.</p></body></html>", "text/html"));
        _fixture.MapPost("/apply", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var got = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            var file = form.Files.GetFile("resume");
            got["resume:file"] = file is null ? "" : $"{file.FileName}:{file.Length}";
            lock (_posts) _posts.Add(got);
            return Results.Content("<html><body><h1>Thank you for applying!</h1><p>Your application has been received.</p></body></html>", "text/html");
        });
        await _fixture.StartAsync();
        _fixtureUrl = _fixture.Urls.First();
        var host = new Uri(_fixtureUrl).Host;
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(host)), "the fixture must be on loopback — never a real site");
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null) await _fixture.StopAsync();
        if (_server is { HasExited: false }) { _server.Kill(entireProcessTree: true); }
        _server?.Dispose();
    }

    private const string FormHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <label for="last_name">Last Name</label><input id="last_name" name="job_application[last_name]" />
          <label for="email">Email</label><input id="email" name="job_application[email]" type="email" />
          <label for="resume">Resume/CV</label><input id="resume" name="resume" type="file" />
          <label for="question_2">Are you legally authorized to work in the United States?</label>
          <select id="question_2" name="job_application[question_2]"><option value=""></option><option>Yes</option><option>No</option></select>
          <label for="question_3">Describe a system you scaled.</label><textarea id="question_3" name="job_application[question_3]"></textarea>
          <label for="question_5">Choose your specialization</label><input id="question_5" name="job_application[question_5]" />
          <fieldset><legend>Do you require visa sponsorship?</legend>
            <label for="q6_yes">Yes</label><input id="q6_yes" type="radio" name="job_application[question_6]" value="Yes" />
            <label for="q6_no">No</label><input id="q6_no" type="radio" name="job_application[question_6]" value="No" />
          </fieldset>
          <label for="question_7">I accept the privacy policy</label>
          <input id="question_7" type="checkbox" name="job_application[question_7]" value="accepted" />
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        </body></html>
        """;

    // The input takes the files; the board's own uploader then errors and nothing is uploaded.
    private const string BrokenUploaderHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <label for="resume">Resume/CV</label><input id="resume" name="resume" type="file" />
          <p id="resume_error"></p>
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        <script>
          document.getElementById('resume').addEventListener('change', () => {
            document.getElementById('resume_error').textContent =
              "Cannot read properties of undefined (reading 'uploadFile')";
          });
        </script>
        </body></html>
        """;

    // The uploader errors on a file, exactly as Greenhouse's does, and Enter manually reveals
    // a textarea that takes the résumé text instead.
    private const string ManualResumeHtml = """
        <html><head><meta charset="utf-8"></head><body>
        <h1>Senior Engineer</h1>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <!-- The country selector's own button, which sorts before the uploader's and must
               not swallow the click — this is what happened on GitLab's form. -->
          <button type="button" aria-label="Clear search"></button>
          <span>Resume/CV</span>
          <input id="resume" name="resume" type="file" />
          <button type="button" id="manual">Enter manually</button>
          <button type="button" id="remove" style="display:none" aria-label="Remove file"></button>
          <p id="resume_error"></p>
          <textarea id="resume_text" name="resume_text" style="display:none"></textarea>
          <label for="cover">Cover Letter</label><input id="cover" name="cover_letter" type="file" />
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        <script>
          const file = document.getElementById('resume');
          const manual = document.getElementById('manual');
          const remove = document.getElementById('remove');
          const err = document.getElementById('resume_error');
          file.addEventListener('change', () => {
            if (file.files.length === 0) return;
            // Holding a file, the widget shows it and a Remove control INSTEAD of its
            // Attach and Enter manually buttons — and its uploader then errors.
            manual.style.display = 'none';
            remove.style.display = 'inline';
            err.textContent = "Cannot read properties of undefined (reading 'uploadFile')";
          });
          remove.addEventListener('click', () => {
            file.value = '';
            err.textContent = '';
            remove.style.display = 'none';
            manual.style.display = 'inline';
          });
          manual.addEventListener('click', () => {
            err.textContent = '';
            // The widget renders its box after the click, not synchronously with it.
            setTimeout(() => { document.getElementById('resume_text').style.display = 'block'; }, 700);
          });
        </script>
        </body></html>
        """;

    // The challenge that actually blocked nine real submissions: nothing on the page until
    // Submit is clicked, and only then an image-grid puzzle from no named vendor.
    private const string CaptchaOnSubmitHtml = """
        <html><body><form id="f" method="post" action="/apply">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <button id="submit_app" type="button">Submit Application</button>
        </form>
        <div id="challenge" class="captcha-overlay" style="display:none;width:400px;height:300px">
          Click the object that does not fit the column pattern
        </div>
        <script>
          document.getElementById('submit_app').addEventListener('click', () => {
            document.getElementById('challenge').style.display = 'block';
          });
        </script>
        </body></html>
        """;

    private const string CaptchaFormHtml = """
        <html><body><form method="post" action="/apply">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <iframe title="reCAPTCHA" src="https://www.google.com/recaptcha/api2/anchor?k=x"
                  style="width:300px;height:74px;border:0"></iframe>
          <button id="submit_app" type="submit">Submit Application</button>
        </form></body></html>
        """;

    // reCAPTCHA v3 rides along invisibly on a great many boards and never blocks a submission.
    private const string BadgeFormHtml = """
        <html><body><form method="post" action="/apply">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <div class="grecaptcha-badge" style="width:256px;height:60px">
            <iframe src="https://www.google.com/recaptcha/api2/anchor?k=v3" style="width:256px;height:60px"></iframe>
          </div>
          <button id="submit_app" type="submit">Submit Application</button>
        </form></body></html>
        """;

    private static AgentPacket OnePlusResumePacket() => new()
    {
        ApplicationName = "acme-senior-engineer.md",
        Provider = "greenhouse",
        Questions =
        [
            new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("resume", "Resume/CV", true, PacketQuestion.File, [], PacketQuestion.Standard),
        ],
        Answers = new() { ["first_name"] = "Ada" },
    };

    private static AgentPacket Packet() => new()
    {
        ApplicationName = "acme-senior-engineer.md",
        Provider = "greenhouse",
        Questions =
        [
            new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("last_name", "Last Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("email", "Email", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("resume", "Resume/CV", true, PacketQuestion.File, [], PacketQuestion.Standard),
            new("question_2", "Are you legally authorized to work in the United States?", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom),
            new("question_3", "Describe a system you scaled.", true, PacketQuestion.Textarea, [], PacketQuestion.Custom),
        ],
        Answers = new()
        {
            ["first_name"] = "Ada", ["last_name"] = "Byte", ["email"] = "ada@example.com",
            ["question_2"] = "Yes", ["question_3"] = "I scaled billing to 10x.",
        },
    };

    private BrowserSubmitter Submitter() =>
        new(new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true, TimeoutSeconds = 60 },
            NullLogger<BrowserSubmitter>.Instance);

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%fake\n");

    [SkippableFact]
    public async Task A_dry_run_fills_and_screenshots_but_never_posts()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", Packet(), (Pdf, "resume.pdf"), dryRun: true);

        Assert.True(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Empty(outcome.Unmapped);
        Assert.Equal(6, outcome.Mapped.Count);
        Assert.NotNull(outcome.Screenshot);
        Assert.True(outcome.Screenshot!.Length > 1000);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_real_run_submits_the_mapped_answers_and_the_resume_and_reads_the_confirmation()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", Packet(), (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("Thank you", outcome.Confirmation);
        var post = Assert.Single(_posts);
        Assert.Equal("Ada", post["job_application[first_name]"]);
        Assert.Equal("Yes", post["job_application[question_2]"]);
        Assert.Equal("I scaled billing to 10x.", post["job_application[question_3]"]);
        Assert.Equal($"resume.pdf:{Pdf.Length}", post["resume:file"]);
    }

    [SkippableFact]
    public async Task An_unmapped_required_answer_refuses_the_click()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = Packet();
        packet.Questions.Add(new("question_9", "Favourite dinosaur", true, PacketQuestion.Text, [], PacketQuestion.Custom));
        packet.Answers["question_9"] = "Stegosaurus";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Equal(["question_9"], outcome.Unmapped);
        Assert.Contains("could not be mapped", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_closed_posting_is_reported_closed_and_never_submits()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/closed", Packet(), (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Closed);
        Assert.False(outcome.Submitted);
        Assert.Contains("no longer open", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_select_answer_that_matches_no_option_is_unmapped_not_silently_filled()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // question_5 is a custom combobox (a plain input, not a native <select>) with a fixed
        // option set. The answer matches none of them — the old code typed it and called it
        // mapped, so the form looked clean. It must now be reported unmapped and refuse the click.
        var packet = Packet();
        packet.Questions.Add(new("question_5", "Choose your specialization", true, PacketQuestion.Select,
            [".NET", "Front End", "DevOps/SRE"], PacketQuestion.Custom));
        packet.Answers["question_5"] = "United States, U.S., USA";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("question_5", outcome.Unmapped);
        Assert.DoesNotContain("question_5", outcome.Mapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_select_answer_that_matches_an_option_still_maps()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = Packet();
        packet.Questions.Add(new("question_5", "Choose your specialization", true, PacketQuestion.Select,
            [".NET", "Front End", "DevOps/SRE"], PacketQuestion.Custom));
        packet.Answers["question_5"] = ".NET";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", packet, (Pdf, "resume.pdf"), dryRun: true);

        Assert.Contains("question_5", outcome.Mapped);
        Assert.Empty(outcome.Unmapped);
    }

    [SkippableFact]
    public async Task A_radio_group_is_checked_not_filled_and_reaches_the_post()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Discovery hands a radio group over as a Select keyed by the group name. The old
        // code typed into it, which Playwright refuses outright ("Input of type \"radio\"
        // cannot be filled") — the exception abandoned the whole run, mapped fields and all.
        var packet = Packet();
        packet.Questions.Add(new("job_application[question_6]", "Do you require visa sponsorship?", true,
            PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom));
        packet.Answers["job_application[question_6]"] = "No";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("job_application[question_6]", outcome.Mapped);
        Assert.Empty(outcome.Unmapped);
        var post = Assert.Single(_posts);
        Assert.Equal("No", post["job_application[question_6]"]);
    }

    [SkippableFact]
    public async Task A_checkbox_is_set_from_the_answers_polarity()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = Packet();
        packet.Questions.Add(new("job_application[question_7]", "I accept the privacy policy", true,
            PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom));
        packet.Answers["job_application[question_7]"] = "Yes";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("job_application[question_7]", outcome.Mapped);
        var post = Assert.Single(_posts);
        Assert.Equal("accepted", post["job_application[question_7]"]);
    }

    [SkippableFact]
    public async Task A_radio_answer_matching_no_member_is_unmapped_not_silently_skipped()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = Packet();
        packet.Questions.Add(new("job_application[question_6]", "Do you require visa sponsorship?", true,
            PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom));
        packet.Answers["job_application[question_6]"] = "Prefer not to say";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("job_application[question_6]", outcome.Unmapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task An_upload_the_board_rejects_is_unmapped_and_refuses_the_click()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // The live failure: Playwright hands over the bytes fine, the board's uploader errors,
        // nothing uploads — and the old code called that mapped and clicked Submit anyway.
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/uploader", OnePlusResumePacket(), (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("resume", outcome.Unmapped);
        Assert.DoesNotContain("resume", outcome.Mapped);
        Assert.Contains("could not be mapped", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_working_upload_still_counts_as_mapped()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", OnePlusResumePacket(), (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("resume", outcome.Mapped);
        var post = Assert.Single(_posts);
        Assert.Equal($"resume.pdf:{Pdf.Length}", post["resume:file"]);
    }

    [SkippableFact]
    public async Task A_board_that_refuses_the_file_takes_the_resume_as_text()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Greenhouse's shape: the uploader throws on the file, so the run falls back to the
        // form's own Enter manually box — and the application goes through with the résumé on it.
        const string text = "Aaron K. Clark — Senior Backend Engineer. 15 years of .NET.";
        var outcome = await Submitter().RunAsync(
            $"{_fixtureUrl}/jobs/manual", OnePlusResumePacket(), (Pdf, "resume.pdf"), dryRun: false,
            resumeText: text);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("resume", outcome.Mapped);
        Assert.Empty(outcome.Unmapped);
        var post = Assert.Single(_posts);
        Assert.Equal(text, post["resume_text"]);
    }

    [SkippableFact]
    public async Task Without_resume_text_a_board_that_refuses_the_file_still_refuses_the_click()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // No text to fall back on, so the honest refusal from #135 still stands.
        var outcome = await Submitter().RunAsync(
            $"{_fixtureUrl}/jobs/manual", OnePlusResumePacket(), (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("resume", outcome.Unmapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task An_interactive_captcha_stops_the_run_before_the_click()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md",
            Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/captcha", packet, null, dryRun: false);

        Assert.True(outcome.Captcha);
        Assert.False(outcome.Submitted);
        Assert.Contains("captcha", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_captcha_raised_by_the_submit_click_is_reported_not_retried()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md",
            Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/captcha-on-submit", packet, null, dryRun: false);

        Assert.True(outcome.Captcha);
        Assert.False(outcome.Submitted);
        Assert.Contains("captcha", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task The_invisible_recaptcha_badge_is_not_treated_as_a_captcha()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md",
            Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/badge", packet, null, dryRun: false);

        Assert.False(outcome.Captcha);
        Assert.True(outcome.Submitted, outcome.Error);
    }

    [Fact]
    public async Task A_private_target_is_refused_before_any_browser_is_touched()
    {
        var submitter = new BrowserSubmitter(new BrowserOptions { Endpoint = "ws://nowhere:1/" }, NullLogger<BrowserSubmitter>.Instance);
        await Assert.ThrowsAsync<AppValidationException>(() =>
            submitter.RunAsync("http://127.0.0.1:8080/jobs/1", Packet(), null, dryRun: true));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            submitter.RunAsync("ftp://example.com/x", Packet(), null, dryRun: true));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
