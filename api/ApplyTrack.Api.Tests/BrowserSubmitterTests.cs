// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
using System.Net;
using System.Text;
using ApplyTrack.Api.Agent;
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
    private readonly List<Dictionary<string, string>> _laterPosts = [];

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
        // LinkedIn's Easy Apply dialog, in the markup the live one has (mapped 2026-09-20, #278):
        // steps that only exist once reached, a validator that flags a placeholder, a pre-ticked
        // "Follow", and a close that asks Discard or Save.
        _fixture.MapGet("/li/jobs/view/1", () => Results.Content(EasyApplyHtml, "text/html"));
        // A "Follow" box that will not stay unticked.
        _fixture.MapGet("/li/jobs/view/sticky-follow", () => Results.Content(EasyApplyHtml.Replace(
            """id="follow-company-checkbox" checked>""", """id="follow-company-checkbox" checked onchange="this.checked=true">"""), "text/html"));
        _fixture.MapGet("/li/login", () => Results.Content("<html><body><h1>Sign in</h1></body></html>", "text/html"));
        _fixture.MapPost("/li/event", async (HttpRequest req) =>
        {
            using var sr = new StreamReader(req.Body);
            var body = await sr.ReadToEndAsync();
            lock (_posts) _posts.Add(new() { ["json"] = body });
            return Results.Json(new { ok = true });
        });
        _fixture.MapGet("/jobs/1", () => Results.Content(FormHtml, "text/html"));
        // Ashby draws its own checkbox and hides the real input: no id, no size, tabindex -1 (#280).
        _fixture.MapGet("/jobs/styled-checkbox", () => Results.Content(FormHtml.Replace(
            """<label for="question_7">I accept the privacy policy</label>""", "").Replace(
            """<input id="question_7" type="checkbox" name="job_application[question_7]" value="accepted" />""",
            """<label><input tabindex="-1" type="checkbox" name="job_application[question_7]" value="accepted" style="position:absolute;width:0;height:0;opacity:0;margin:0" /><span style="display:inline-block;width:18px;height:18px;border:1px solid #333"></span> I accept the privacy policy</label>"""), "text/html"));
        // Ashby's yes/no: two buttons and a hidden checkbox that only mirrors them. The form will
        // not post until one is pressed, exactly as Ashby's validation will not (#280).
        _fixture.MapGet("/jobs/ashby-yesno", () => Results.Content(FormHtml.Replace(
            """<label for="question_7">I accept the privacy policy</label>""", "").Replace(
            """<input id="question_7" type="checkbox" name="job_application[question_7]" value="accepted" />""",
            """
            <label for="visa">Will you now, or in the future, require employer visa sponsorship?</label>
            <div class="ashby-application-form-input-yesno">
              <button type="button" data-option="yes" aria-pressed="false" onclick="pick(this)">Yes</button>
              <button type="button" data-option="no" aria-pressed="false" onclick="pick(this)">No</button>
              <input type="checkbox" tabindex="-1" name="visa" style="position:absolute;width:0;height:0;opacity:0;margin:0" />
              <input type="hidden" name="visa_answer" id="visa_answer" value="" />
            </div>
            <script>
              function pick(b) {
                for (const x of b.parentElement.querySelectorAll('button')) x.setAttribute('aria-pressed', String(x === b));
                document.getElementById('visa_answer').value = b.dataset.option;
                b.parentElement.querySelector('input[type=checkbox]').checked = b.dataset.option === 'yes';
              }
              document.addEventListener('submit', e => { if (!document.getElementById('visa_answer').value) e.preventDefault(); }, true);
            </script>
            """), "text/html"));
        // Allstate: the Apply link's accessible name opens with the job title (#280).
        _fixture.MapGet("/jobs/apply-named-for-the-job", () => Results.Content(
            """<html><body><h1>Senior Engineer</h1><p>No form on this page.</p><a class="button apply-btn" href="/jobs/1" role="button" aria-label="Senior Engineer Apply Now open in new window">Apply now </a></body></html>""", "text/html"));
        // A board whose own uploader rejects the files it was handed — Greenhouse's live
        // failure, reproduced: the input accepts them, the uploader then errors, nothing uploads.
        _fixture.MapGet("/jobs/uploader", () => Results.Content(BrokenUploaderHtml, "text/html"));
        // Greenhouse's shape: the uploader rejects the file, but the form offers to take the
        // résumé as text instead.
        _fixture.MapGet("/jobs/manual", () => Results.Content(ManualResumeHtml, "text/html"));
        // Greenhouse's current résumé widget as GitLab's board renders it (#195): the required
        // flag is on the upload group, not the file input; the uploader fails ?err= ms after
        // the attach; Enter manually opens a textarea inside the group.
        _fixture.MapGet("/jobs/gh-resume", () => Results.Content(GreenhouseResumeHtml, "text/html"));
        // A challenge that only appears once Submit is clicked.
        _fixture.MapGet("/jobs/captcha-on-submit", () => Results.Content(CaptchaOnSubmitHtml, "text/html"));
        // A form guarded by an interactive captcha.
        _fixture.MapGet("/jobs/captcha", () => Results.Content(CaptchaFormHtml, "text/html"));
        // The invisible reCAPTCHA v3 badge, which must NOT count as a captcha.
        _fixture.MapGet("/jobs/badge", () => Results.Content(BadgeFormHtml, "text/html"));
        // Greenhouse's current form: react-select comboboxes for country, city (an async
        // autocomplete) and the custom questions, each with a hidden required input.
        _fixture.MapGet("/jobs/react-select", () => Results.Content(ReactSelectHtml, "text/html"));
        // A form whose own validation blocks Submit on a field the DOM never marks required.
        _fixture.MapGet("/jobs/validating", () => Results.Content(ValidatingFormHtml, "text/html"));
        // A form that re-mounts itself on Submit instead of posting.
        _fixture.MapGet("/jobs/resetting", () => Results.Content(ResettingFormHtml, "text/html"));
        // Greenhouse's real shape: the click posts as JSON in the background and the page
        // shows its verdict seconds later — a confirmation, or an error line.
        _fixture.MapGet("/jobs/slow", () => Results.Content(SlowFormHtml.Replace("OUTCOME", "ok"), "text/html"));
        _fixture.MapGet("/jobs/slow-error", () => Results.Content(SlowFormHtml.Replace("OUTCOME", "error"), "text/html"));
        // Greenhouse's captcha fallback: 428 with the address it emailed a security code to,
        // then one box per character on the form and a second Submit.
        _fixture.MapGet("/jobs/security-code", () => Results.Content(SecurityCodeHtml, "text/html"));
        _fixture.MapPost("/apply-code.json", async (HttpRequest req) =>
        {
            using var sr = new StreamReader(req.Body);
            var body = await sr.ReadToEndAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("security_code", out var sc) || sc.GetString() != "IEG0PXWR")
                return Results.Json(new { code = "captcha-failed", message = "Oops! We were unable to verify your Captcha response.", security_code_recipient = "ada@example.com" }, statusCode: 428);
            lock (_posts) _posts.Add(new() { ["json"] = body });
            return Results.Json(new { ok = true });
        });
        _fixture.MapPost("/apply.json", async (HttpRequest req) =>
        {
            using var sr = new StreamReader(req.Body);
            var body = await sr.ReadToEndAsync();
            lock (_posts) _posts.Add(new() { ["json"] = body });
            return Results.Json(new { ok = true });
        });
        // Greenhouse's posting page: an "Apply" button above the description that only
        // scrolls to the form, and the real "Submit application" at the bottom of it.
        _fixture.MapGet("/jobs/anchor", () => Results.Content(ApplyAnchorHtml, "text/html"));
        // join.com's posting page (#210): an "Apply later" email box with its own submit,
        // and the real form only behind "Apply now". Clicking Apply later emails the
        // candidate the posting's link and sends no application.
        _fixture.MapGet("/jobs/apply-later", () => Results.Content(ApplyLaterHtml, "text/html"));
        _fixture.MapPost("/apply-later", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            lock (_laterPosts) _laterPosts.Add(form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()));
            return Results.Content("<html><body><p>We sent the link to your email.</p></body></html>", "text/html");
        });
        // join.com's page behind Apply: an email box and Continue — a sign-in, not a form.
        _fixture.MapGet("/jobs/sign-in", () => Results.Content(SignInGateHtml, "text/html"));
        // The whole of join.com's way in (#210): the posting with its Apply-later trap, Apply now
        // to the email sign-in, Continue, then a code box the emailed code goes into — or a
        // "check your inbox" with nothing to type, and the emailed link opens the form.
        _fixture.MapGet("/jobs/sign-in-code", () => Results.Content(SignInPostingHtml.Replace("MODE", "code"), "text/html"));
        _fixture.MapGet("/jobs/sign-in-link", () => Results.Content(SignInPostingHtml.Replace("MODE", "link"), "text/html"));
        _fixture.MapGet("/jobs/sign-in-code/auth", () => Results.Content(SignInAuthHtml.Replace("MODE", "code"), "text/html"));
        _fixture.MapGet("/jobs/sign-in-link/auth", () => Results.Content(SignInAuthHtml.Replace("MODE", "link"), "text/html"));
        _fixture.MapGet("/jobs/sign-in-code/form", (string email) => Results.Content(SignedInFormHtml.Replace("EMAIL", email), "text/html"));
        _fixture.MapGet("/jobs/sign-in-link/verify", (string token) => token == "abc123"
            ? Results.Content(SignedInFormHtml.Replace("EMAIL", "ada@example.com"), "text/html")
            : Results.Content("<html><body><h1>This link has expired.</h1></body></html>", "text/html"));
        // A posting taken down outright: Greenhouse's 404 page, no gone-notice, no form.
        _fixture.MapGet("/jobs/gone", () => Results.Content(
            "<html><head><title>Page not found</title></head><body><h1>Page not found</h1>"
            + "<p>The page you were looking for doesn't exist.</p></body></html>", "text/html"));
        // A résumé and a cover letter, each a file field with its own Enter manually box.
        _fixture.MapGet("/jobs/cover", () => Results.Content(CoverLetterHtml, "text/html"));
        // A posting that closed between discovery and now: the board's gone-notice, no form.
        _fixture.MapGet("/jobs/closed", () => Results.Content(
            "<html><body><h1>Current openings at Acme</h1>"
            + "<p>The job you are looking for is no longer open.</p></body></html>", "text/html"));
        // Comeet's shape: the posting page has only an "Apply for this job" button, and the
        // click loads the form into an iframe. Nothing fillable ever lives in the top document.
        _fixture.MapGet("/jobs/framed", () => Results.Content(FramedHtml, "text/html"));
        // Ashby's shape after the click: the API answers 200, then the page says the
        // application was flagged as spam. No code, no challenge — a person's job.
        _fixture.MapGet("/jobs/spam-on-submit", () => Results.Content(SpamOnSubmitHtml, "text/html"));
        // A résumé input that is called cv, labelled "Attach Resume".
        _fixture.MapGet("/jobs/cv", () => Results.Content(CvHtml, "text/html"));
        _fixture.MapGet("/jobs/framed-form", () => Results.Content(FormHtml, "text/html"));
        // Ashby's shape: the form is fetched after the page is idle, and asks for one Name.
        _fixture.MapGet("/jobs/late-name", () => Results.Content(LateNameHtml, "text/html"));
        // A form whose labels are plain text above each box — no <label for>, no aria.
        _fixture.MapGet("/jobs/text-labels", () => Results.Content(TextLabelsHtml, "text/html"));
        // Comeet's shape (Kendago): an optional "LinkedIn Profile URL" in the standard section,
        // then a required "LinkedIn profile" in the custom section (#187).
        _fixture.MapGet("/jobs/two-linkedin", () => Results.Content(TwoLinkedInHtml, "text/html"));
        // Workable's job finder: a search box on the posting page, and an Apply link to the
        // form on another page of the same site (#180).
        _fixture.MapGet("/jobs/search-first", () => Results.Content(SearchFirstHtml, "text/html"));
        // A SuccessFactors career site's posting (Kiewit, #214): a keyword/location job search
        // and a job-alert box on the page, the form behind "Apply now »" — on the same site
        // here; on the real one it leads to career4.successfactors.com's sign-in.
        _fixture.MapGet("/jobs/widgets", () => Results.Content(WidgetsHtml.Replace("APPLY_HREF", "/jobs/1"), "text/html"));
        // Alignerr's way in: Apply opens the marketplace's sign-up, which is a terms checkbox
        // and "Continue with Google / LinkedIn" — no form, no username box.
        _fixture.MapGet("/jobs/widgets-sso", () => Results.Content(WidgetsHtml.Replace("APPLY_HREF", "/jobs/sso-wall"), "text/html"));
        _fixture.MapGet("/jobs/sso-wall", () => Results.Content(SsoWallHtml, "text/html"));
        // The same wall in a new tab that only shows its content after a round trip (Alignerr, #235).
        _fixture.MapGet("/jobs/widgets-sso-late", () => Results.Content(WidgetsHtml.Replace("href=\"APPLY_HREF\"", "href=\"#\" onclick=\"const w = window.open('', '_blank'); setTimeout(() => { w.location = '/jobs/sso-wall'; }, 3000); return false;\""), "text/html"));
        // Gainwell's posting: "Apply Now" only opens a menu, and the way in is its "Apply Now" item.
        _fixture.MapGet("/jobs/menu-apply", () => Results.Content(MenuApplyHtml, "text/html"));
        _fixture.MapGet("/jobs/widgets-account",() => Results.Content(WidgetsHtml.Replace("APPLY_HREF", "https://career4.successfactors.com/careers?company=Kiewit"), "text/html"));
        // SAP SuccessFactors' way in (#216): the career-site posting, Apply now to the board's
        // sign-in, and behind it the application as an accordion — documents already on the
        // account, profile pre-filled, job-specific questions folded away — with Apply in a
        // footer bar and an "are you sure" dialog before it sends.
        _fixture.MapGet("/jobs/sf", () => Results.Content(WidgetsHtml.Replace("APPLY_HREF", "/jobs/sf/signin"), "text/html"));
        _fixture.MapGet("/jobs/sf/signin", (string? error) => Results.Content(SfSignInHtml.Replace("ERROR", error is null ? "" : "<p class=\"errorMessage\">Invalid email address or password.</p>"), "text/html"));
        _fixture.MapPost("/jobs/sf/signin", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            if (form["password"] != "Sekrit-9$") return Results.Redirect("/jobs/sf/signin?error=1");
            return form["username"].ToString() switch
            {
                "ada@example.com" => Results.Redirect("/jobs/sf/app"),
                // The candidate who applied by hand: the board shows a notice, not the form (#222).
                "again@example.com" => Results.Redirect("/jobs/sf/applied"),
                _ => Results.Redirect("/jobs/sf/signin?error=1"),
            };
        });
        _fixture.MapGet("/jobs/sf/app", () => Results.Content(SfApplicationHtml, "text/html"));
        _fixture.MapGet("/jobs/sf/applied", () => Results.Content(
            "<html><body><form name=\"keywordsearch\" role=\"search\"><input type=\"text\" name=\"q\" placeholder=\"Search by Keyword\" /></form>"
            + "<h1>Career Opportunities: Sr AI Engineer</h1><p>You already applied for this position.</p><a href=\"/jobs\">Back to Job Listings</a></body></html>", "text/html"));
        // A posting whose Apply button never becomes clickable (Fuse Energy).
        _fixture.MapGet("/jobs/stuck-apply", () => Results.Content(StuckApplyHtml, "text/html"));
        // Paycor's SubmitResume: a pre-screening step (a sponsorship radio and Continue) before
        // the real form, which Continue navigates to (#239).
        _fixture.MapGet("/jobs/prescreen", () => Results.Content(PreScreenHtml, "text/html"));
        // A cookie banner over the posting page (Zoho Recruit, Workable's job finder): the
        // Apply link is there, but nothing gets clicked through the overlay (#202).
        _fixture.MapGet("/jobs/consent", () => Results.Content(ConsentHtml.Replace("BODY", ConsentPostingBody), "text/html"));
        // The same banner, over the form itself.
        _fixture.MapGet("/jobs/consent-form", () => Results.Content(ConsentHtml.Replace("BODY", FormHtml), "text/html"));
        // Radancy (TalentBrew), as UnitedHealth Group runs it: a dialog named for nothing but
        // "Important System Message", its only mention of cookies in the text (#283) — once
        // with Radancy's own button id, once as any such dialog a vendor we have not met ships.
        _fixture.MapGet("/jobs/consent-unnamed", () => Results.Content(UnnamedConsentHtml.Replace("BUTTONID", "system-ialert-button").Replace("BODY", ConsentPostingBody), "text/html"));
        _fixture.MapGet("/jobs/consent-unnamed-generic", () => Results.Content(UnnamedConsentHtml.Replace("BUTTONID", "ok").Replace("BODY", ConsentPostingBody), "text/html"));
        // No role, no telling name, and "Accept" is a link: just a bar pinned to the bottom.
        _fixture.MapGet("/jobs/consent-pinned-link", () => Results.Content(PinnedLinkConsentHtml.Replace("BODY", ConsentPostingBody), "text/html"));
        // A dialog that mentions cookies but holds the form itself: its "I agree" is an answer.
        _fixture.MapGet("/jobs/consent-is-the-form", () => Results.Content(AgreeInsideFormDialogHtml.Replace("BODY", FormHtml), "text/html"));
        // The same, where all the dialog holds besides the button is an acknowledgement checkbox.
        _fixture.MapGet("/jobs/consent-is-an-acknowledgement", () => Results.Content(AgreeBesideCheckboxHtml.Replace("BODY", FormHtml), "text/html"));
        // Zoho Recruit's consent: rendered after the page has booted, with a transparent
        // freeze layer over the whole viewport until a choice is made (fyerx).
        _fixture.MapGet("/jobs/consent-late", () => Results.Content(LateFreezeConsentHtml, "text/html"));
        // Workable's answer for a job taken down: HTTP 410 and a page that says so (#203).
        _fixture.MapGet("/jobs/gone-410", () => Results.Content(
            "<html><head><title>This job is not available anymore</title></head><body>"
            + "<h1>This job is not available anymore</h1><p>Browse other jobs.</p></body></html>",
            "text/html", statusCode: 410));
        // A page with no form on it at all.
        _fixture.MapGet("/jobs/blank", () => Results.Content(
            "<html><body><h1>Senior Engineer</h1><p>We are hiring. Email us your CV.</p></body></html>", "text/html"));
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

    private const string FramedHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <p>Description of the job.</p>
        <button type="button" id="open" onclick="const f=document.createElement('iframe');f.src='/jobs/framed-form';f.style.width='700px';f.style.height='900px';document.getElementById('slot').appendChild(f);this.remove()">Apply for this job</button>
        <div id="slot"></div>
        </body></html>
        """;

    private const string SpamOnSubmitHtml = """
        <html><body><form id="f" method="post" action="/apply.json">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <button id="submit_app" type="button">Submit Application</button>
        </form>
        <div id="refused" style="display:none">
          <strong>We couldn't submit your application</strong>
          <p>Your application submission was flagged as possible spam. If you believe this was a mistake, please submit your application again.</p>
        </div>
        <script>
          document.getElementById('submit_app').addEventListener('click', async () => {
            await fetch('/apply.json', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{"probe":true}' });
            document.getElementById('f').style.display = 'none';
            document.getElementById('refused').style.display = 'block';
          });
        </script>
        </body></html>
        """;

    private const string CvHtml = """
        <html><body>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="firstName">First name *</label><input id="firstName" name="firstName" required />
          <label for="lastName">Last name *</label><input id="lastName" name="lastName" required />
          <label for="email">Email *</label><input id="email" name="email" type="email" required />
          <label for="cv">Attach Resume *</label><input id="cv" name="resume" type="file" required />
          <button type="submit">Submit application</button>
        </form>
        </body></html>
        """;

    private const string LateNameHtml = """
        <html><body>
        <h1>Developer Experience Engineer</h1>
        <div id="slot"><p>Fetching application form</p></div>
        <script>
          setTimeout(() => { document.getElementById('slot').innerHTML = `
            <form method="post" action="/apply" enctype="multipart/form-data">
              <label for="_systemfield_name">Name *</label><input id="_systemfield_name" name="_systemfield_name" required />
              <label for="_systemfield_email">Email *</label><input id="_systemfield_email" name="_systemfield_email" type="email" required />
              <label for="resume">Resume *</label><input id="resume" name="resume" type="file" />
              <button type="submit">Submit Application</button>
            </form>`; }, 1500);
        </script>
        </body></html>
        """;

    private const string TextLabelsHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <div class="field"><div class="caption">First name *</div><input name="f_1" required /></div>
          <div class="field"><div class="caption">Last name *</div><input name="f_2" required /></div>
          <div class="field"><div class="caption">Email *</div><input name="f_3" type="email" required /></div>
          <div class="field"><div class="caption">Resume *</div><input name="resume" type="file" /></div>
          <button type="submit">Submit application</button>
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

    // GitLab's Greenhouse form, as inspected live for #195: `required` lives on the upload
    // group's aria-required, never on the file input (which is visually hidden behind a
    // styled Attach button); Enter manually reveals textarea#resume_text inside the group;
    // a staged file shows its name and a Remove control instead of the buttons. The
    // uploader's failure arrives ?err= ms after the attach — on the live form it came well
    // after the attach had looked good.
    private const string GreenhouseResumeHtml = """
        <html><head><meta charset="utf-8">
        <style>.visually-hidden{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)}</style>
        </head><body>
        <h1>Senior Engineer</h1>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <div role="group" aria-labelledby="upload-label-resume" aria-required="true" class="file-upload">
            <div id="upload-label-resume" class="label">Resume/CV<span class="required">*</span></div>
            <div id="resume_buttons">
              <div><button type="button">Attach</button><label class="visually-hidden" for="resume">Attach</label>
                <input id="resume" name="resume" type="file" class="visually-hidden" /></div>
              <div><button type="button" id="resume_manual">Enter manually</button><label class="visually-hidden" for="resume_text">Enter manually</label></div>
            </div>
            <div id="resume_staged" style="display:none"><span id="resume_name"></span> <button type="button" id="resume_remove" aria-label="Remove file">✕</button></div>
            <p id="resume_error"></p>
            <div id="resume_text_wrap" style="display:none"><textarea id="resume_text" name="resume_text" rows="6"></textarea></div>
          </div>
          <div role="group" aria-labelledby="upload-label-cover_letter" aria-required="false" class="file-upload">
            <div id="upload-label-cover_letter" class="label">Cover Letter</div>
            <div><button type="button">Attach</button><input id="cover_letter" name="cover_letter" type="file" class="visually-hidden" /></div>
            <div><button type="button" id="cover_manual">Enter manually</button></div>
            <div id="cover_text_wrap" style="display:none"><textarea id="cover_letter_text" name="cover_letter_text" rows="6"></textarea></div>
          </div>
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        <script>
          const err = Number(new URLSearchParams(location.search).get('err') || 0);
          const file = document.getElementById('resume');
          const buttons = document.getElementById('resume_buttons');
          const staged = document.getElementById('resume_staged');
          const error = document.getElementById('resume_error');
          file.addEventListener('change', () => {
            if (file.files.length === 0) return;
            buttons.style.display = 'none';
            staged.style.display = 'block';
            document.getElementById('resume_name').textContent = file.files[0].name;
            setTimeout(() => {
              if (file.files.length > 0) error.textContent = "Cannot read properties of undefined (reading 'uploadFile')";
            }, err);
          });
          document.getElementById('resume_remove').addEventListener('click', () => {
            file.value = '';
            error.textContent = '';
            staged.style.display = 'none';
            buttons.style.display = 'block';
          });
          document.getElementById('resume_manual').addEventListener('click', () => {
            setTimeout(() => { document.getElementById('resume_text_wrap').style.display = 'block'; }, 300);
          });
          // The cover letter's box takes its time, which is what puts the judgement of the
          // form after the résumé uploader's late failure in the tests that need it.
          document.getElementById('cover_manual').addEventListener('click', () => {
            setTimeout(() => { document.getElementById('cover_text_wrap').style.display = 'block'; }, 1500);
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

    // Modelled on job-boards.greenhouse.io as inspected live: the visible input is
    // role=combobox with aria-required and NO name; the committed value lives in a hidden
    // required input beside it and in a single-value element; typing alone commits nothing.
    private const string ReactSelectHtml = """
        <html><head><meta charset="utf-8"><style>
          [role=listbox][hidden] { display: none }
          .requiredInput { opacity: 0; width: 1px; height: 1px; position: absolute }
        </style></head><body>
        <h1>Senior Engineer</h1>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name*</label><input id="first_name" name="job_application[first_name]" aria-required="true" />
          <label id="country-label">Country*</label>
          <div class="select-shell" data-select="country">
            <div class="select__control"><div class="select__value-container">
              <div class="select__placeholder">Select a country</div>
              <div class="select__input-container"><input id="country" role="combobox" aria-autocomplete="list" aria-required="true" aria-labelledby="country-label" aria-controls="country-listbox" /></div>
            </div></div>
            <input required tabindex="-1" class="requiredInput" />
            <div id="country-listbox" role="listbox" hidden>
              <div role="option" data-value="US">United States +1</div>
              <div role="option" data-value="UM">United States Minor Outlying Islands +1</div>
              <div role="option" data-value="GB">United Kingdom +44</div>
              <div role="option" data-value="CA">Canada +1</div>
            </div>
            <input type="hidden" name="country_value" />
          </div>
          <label id="candidate-location-label">Location (City)*</label>
          <div class="select-shell" data-select="candidate-location" data-async="1">
            <div class="select__control"><div class="select__value-container">
              <div class="select__placeholder">Start typing a city</div>
              <div class="select__input-container"><input id="candidate-location" role="combobox" aria-autocomplete="list" aria-required="true" aria-labelledby="candidate-location-label" aria-controls="candidate-location-listbox" /></div>
            </div></div>
            <input required tabindex="-1" class="requiredInput" />
            <div id="candidate-location-listbox" role="listbox" hidden>
              <div role="option" data-value="Omaha, Nebraska, United States">Omaha, Nebraska, United States</div>
              <div role="option" data-value="South Omaha, Nebraska, United States">South Omaha, Nebraska, United States</div>
            </div>
            <input type="hidden" name="location_value" />
          </div>
          <label for="resume">Resume/CV</label><input id="resume" name="resume" type="file" />
          <label id="question_9-label">Are you willing to submit to a background check?*</label>
          <div class="select-shell" data-select="question_9">
            <div class="select__control"><div class="select__value-container">
              <div class="select__placeholder">Select...</div>
              <div class="select__input-container"><input id="question_9" role="combobox" aria-autocomplete="list" aria-required="true" aria-labelledby="question_9-label" aria-controls="question_9-listbox" /></div>
            </div></div>
            <input required tabindex="-1" class="requiredInput" />
            <div id="question_9-listbox" role="listbox" hidden>
              <div role="option" data-value="1">Yes</div>
              <div role="option" data-value="0">No</div>
            </div>
            <input type="hidden" name="question_9_value" />
          </div>
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        <script>
          for (const shell of document.querySelectorAll('.select-shell')) {
            const input = shell.querySelector('[role=combobox]');
            const list = shell.querySelector('[role=listbox]');
            const hidden = shell.querySelector('input.requiredInput');
            const out = shell.querySelector('input[type=hidden]');
            const container = shell.querySelector('.select__value-container');
            const open = () => {
              const q = input.value.trim().toLowerCase();
              let any = false;
              for (const o of list.querySelectorAll('[role=option]')) {
                // The city picker is a geocoder: it suggests for whatever was typed — unless
                // the text carries an aside it cannot place, like the real one.
                const show = shell.dataset.async ? (q.length > 0 && !q.includes('(') && !q.includes('nowhere')) : o.textContent.toLowerCase().startsWith(q);
                o.style.display = show ? '' : 'none'; any = any || show;
              }
              list.hidden = !any;
            };
            input.addEventListener('input', () => shell.dataset.async ? setTimeout(open, 400) : open());
            input.addEventListener('click', open);
            const commit = (o) => {
              hidden.value = o.dataset.value; out.value = o.dataset.value;
              let sv = container.querySelector('.select__single-value');
              if (!sv) { sv = document.createElement('div'); sv.className = 'select__single-value'; container.prepend(sv); }
              sv.textContent = o.textContent; container.querySelector('.select__placeholder')?.remove();
              input.value = ''; list.hidden = true;
            };
            for (const o of list.querySelectorAll('[role=option]')) o.addEventListener('click', () => commit(o));
            input.addEventListener('keydown', (e) => {
              if (e.key !== 'Enter') return;
              e.preventDefault();
              const first = [...list.querySelectorAll('[role=option]')].find(o => o.style.display !== 'none');
              if (first && !list.hidden) commit(first);
            });
          }
          // The board's own validation: nothing leaves while a required input is empty.
          document.querySelector('form').addEventListener('submit', (e) => {
            if ([...document.querySelectorAll('input[required], input[aria-required=true]')].some(i => !i.value.trim() && i.getAttribute('role') !== 'combobox')) e.preventDefault();
          });
        </script>
        </body></html>
        """;

    // The DOM says nothing about email being required; only the form's own submit handler does.
    private const string ValidatingFormHtml = """
        <html><body>
        <form method="post" action="/apply">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <label for="email">Email</label><input id="email" name="job_application[email]" aria-describedby="email-error" />
          <p id="email-error" class="helper-text helper-text--error" style="display:none">Email is required.</p>
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        <script>
          document.querySelector('form').addEventListener('submit', (e) => {
            if (!document.getElementById('email').value.trim()) {
              e.preventDefault();
              document.getElementById('email-error').style.display = 'block';
              document.getElementById('email').setAttribute('aria-invalid', 'true');
            }
          });
        </script>
        </body></html>
        """;

    private const string ResettingFormHtml = """
        <html><body>
        <form method="post" action="/apply">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        <script>
          document.querySelector('form').addEventListener('submit', (e) => { e.preventDefault(); e.target.reset(); });
        </script>
        </body></html>
        """;

    // Greenhouse's shape for both documents: a file input, an Attach button and an Enter
    // manually button per section, the résumé's first in the DOM.
    private const string CoverLetterHtml = """
        <html><head><meta charset="utf-8"></head><body>
        <h1>Senior Engineer</h1>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <span>Resume/CV*</span>
          <input id="resume" name="resume" type="file" />
          <button type="button" id="resume_manual">Enter manually</button>
          <textarea id="resume_text" name="resume_text" style="display:none"></textarea>
          <span>Cover Letter*</span>
          <input id="cover_letter" name="cover_letter" type="file" />
          <button type="button" id="cover_manual">Enter manually</button>
          <textarea id="cover_letter_text" name="cover_letter_text" style="display:none"></textarea>
          <button id="submit_app" type="submit">Submit Application</button>
        </form>
        <script>
          document.getElementById('resume_manual').addEventListener('click', () => {
            setTimeout(() => { document.getElementById('resume_text').style.display = 'block'; }, 300);
          });
          document.getElementById('cover_manual').addEventListener('click', () => {
            setTimeout(() => { document.getElementById('cover_letter_text').style.display = 'block'; }, 300);
          });
        </script>
        </body></html>
        """;

    private const string ApplyLaterHtml = """
        <html><body>
        <h1>Founding Engineer</h1>
        <div id="apply-window">
          <button type="button" data-testid="ApplyButton" onclick="document.getElementById('real').style.display='block'">Apply now</button>
        </div>
        <p>The posting, at length.</p>
        <div data-testid="ApplyLater">
          <p>No time right now? Apply later.</p><p>We will send a link to this job to your email.</p>
          <form method="post" action="/apply-later">
            <input id="ApplyLaterEmail" name="email" type="email" placeholder="E-mail" required />
            <button type="submit" data-testid="ApplyLaterSubmitButton">Apply later</button>
          </form>
        </div>
        <form id="real" method="post" action="/apply" style="display:none">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <label for="email">Email</label><input id="email" name="job_application[email]" type="email" />
          <button type="submit">Submit application</button>
        </form>
        </body></html>
        """;

    private const string SignInPostingHtml = """
        <html><body>
        <h1>Founding Engineer</h1>
        <div id="apply-window">
          <button type="button" data-testid="ApplyButton" onclick="location.href='/jobs/sign-in-MODE/auth'">Apply now</button>
        </div>
        <p>The posting, at length.</p>
        <div data-testid="ApplyLater">
          <p>No time right now? Apply later.</p><p>We will send a link to this job to your email.</p>
          <form method="post" action="/apply-later">
            <input id="ApplyLaterEmail" name="email" type="email" placeholder="E-mail" required />
            <button type="submit" data-testid="ApplyLaterSubmitButton">Apply later</button>
          </form>
        </div>
        </body></html>
        """;

    private const string SignInAuthHtml = """
        <html><body>
        <h1>Founding Engineer</h1>
        <div id="step-email">
          <label for="email">Your email address</label><input id="email" name="email" type="email" data-testid="CandidateEmail" />
          <button type="button" id="continue" data-testid="ContinueButton">Continue</button>
          <button type="button" data-testid="GoogleIdentityButton">Continue with Google</button>
        </div>
        <div id="step-code" style="display:none">
          <p>We emailed a 6-digit code to <span id="to"></span>. Enter it to continue.</p>
          <label for="otp">Code</label><input id="otp" autocomplete="one-time-code" inputmode="numeric" maxlength="6" />
          <button type="button" id="verify">Verify</button>
          <p id="bad" class="error" style="display:none">That code is not right.</p>
        </div>
        <div id="step-link" style="display:none">
          <p>Check your inbox — we sent a link to <span id="to2"></span>. Click it to continue your application.</p>
        </div>
        <script>
          document.getElementById('continue').addEventListener('click', () => {
            const email = document.getElementById('email').value;
            setTimeout(() => {
              document.getElementById('step-email').style.display = 'none';
              if ('MODE' === 'code') { document.getElementById('to').textContent = email; document.getElementById('step-code').style.display = 'block'; }
              else { document.getElementById('to2').textContent = email; document.getElementById('step-link').style.display = 'block'; }
            }, 700);
          });
          document.getElementById('verify').addEventListener('click', () => {
            if (document.getElementById('otp').value === '482913') location.href = '/jobs/sign-in-code/form?email=' + encodeURIComponent(document.getElementById('email').value);
            else document.getElementById('bad').style.display = 'block';
          });
        </script>
        </body></html>
        """;

    private const string SignedInFormHtml = """
        <html><body>
        <h1>Founding Engineer</h1>
        <p>Signed in as EMAIL</p>
        <form id="real" method="post" action="/apply">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <label for="last_name">Last Name</label><input id="last_name" name="job_application[last_name]" />
          <label for="email">Email</label><input id="email" name="job_application[email]" type="email" value="EMAIL" />
          <button type="submit">Submit application</button>
        </form>
        </body></html>
        """;

    private const string SignInGateHtml = """
        <html><body>
        <h1>Founding Engineer</h1>
        <form>
          <label for="email">Your email address</label><input id="email" name="email" type="email" data-testid="CandidateEmail" />
          <button type="button" data-testid="ContinueButton">Continue</button>
          <button type="button" data-testid="GoogleIdentityButton">Continue with Google</button>
        </form>
        </body></html>
        """;

    private const string ApplyAnchorHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <button type="button" id="apply_anchor" onclick="document.getElementById('form').scrollIntoView()">Apply</button>
        <p style="height:2400px">The posting, at length.</p>
        <form id="form" method="post" action="/apply">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <button type="submit">Submit application</button>
        </form>
        </body></html>
        """;

    private const string SecurityCodeHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <button type="button">Apply</button>
        <form id="f">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <div id="code" style="display:none">
            <p>A verification code was sent to ada@example.com. To submit your application, enter the 8-character code to confirm you're a human.</p>
            <label>Security code</label>
            <input id="security-input-0" maxlength="1" /><input id="security-input-1" maxlength="1" /><input id="security-input-2" maxlength="1" /><input id="security-input-3" maxlength="1" />
            <input id="security-input-4" maxlength="1" /><input id="security-input-5" maxlength="1" /><input id="security-input-6" maxlength="1" /><input id="security-input-7" maxlength="1" />
          </div>
          <button id="submit_btn" type="submit">Submit application</button>
        </form>
        <script>
          const boxes = [...document.querySelectorAll('[id^=security-input-]')];
          boxes.forEach((b, i) => b.addEventListener('input', () => { if (b.value && boxes[i + 1]) boxes[i + 1].focus(); document.getElementById('submit_btn').disabled = boxes.some(x => !x.value); }));
          document.getElementById('f').addEventListener('submit', async (e) => {
            e.preventDefault();
            await new Promise(r => setTimeout(r, 1500));
            const code = boxes.map(b => b.value).join('').toUpperCase();
            const res = await fetch('/apply-code.json', { method: 'POST', headers: { 'content-type': 'application/json' },
              body: JSON.stringify({ job_application: { first_name: document.getElementById('first_name').value }, security_code: code || null }) });
            if (res.status === 428) { document.getElementById('code').style.display = 'block'; document.getElementById('submit_btn').disabled = true; return; }
            document.body.innerHTML = '<h1>Thank you for applying!</h1>';
          });
        </script>
        </body></html>
        """;

    private const string SlowFormHtml = """
        <html><body>
        <form id="f">
          <label for="first_name">First Name</label><input id="first_name" name="job_application[first_name]" />
          <button id="submit_app" type="submit">Submit application</button>
        </form>
        <p id="msg" class="helper-text helper-text--error" style="display:none"></p>
        <script>
          document.getElementById('f').addEventListener('submit', async (e) => {
            e.preventDefault();
            await new Promise(r => setTimeout(r, 2500));   // the captcha, then the request
            if ('OUTCOME' === 'ok') {
              await fetch('/apply.json', { method: 'POST', headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ job_application: { first_name: document.getElementById('first_name').value } }) });
              await new Promise(r => setTimeout(r, 1500));
              document.body.innerHTML = '<h1>Thank you for applying!</h1><p>Your application has been submitted.</p>';
            } else {
              const m = document.getElementById('msg');
              m.textContent = 'There was an error processing your application. Please try again.';
              m.style.display = 'block';
            }
          });
        </script>
        </body></html>
        """;

    private const string TwoLinkedInHtml = """
        <html><body>
        <form method="post" action="/apply" enctype="multipart/form-data">
          <label for="first_name">First Name</label><input id="first_name" name="first_name" />
          <label for="li_url">LinkedIn Profile URL</label><input id="li_url" name="li_url" />
          <h3>Additional questions</h3>
          <label for="li_req">LinkedIn profile *</label><input id="li_req" name="li_req" required />
          <button type="submit">Submit application</button>
        </form>
        </body></html>
        """;

    private const string WidgetsHtml = """
        <html><body>
        <form class="form-inline jobAlertsSearchForm" name="keywordsearch" method="get" action="/search/" role="search">
          <input type="text" class="keywordsearch-q columnized-search" name="q" placeholder="Search by Keyword" aria-label="Search by Keyword" />
          <input type="text" class="keywordsearch-locationsearch columnized-search" name="locationsearch" placeholder="Search by Location" aria-label="Search by Location" />
          <select name="optionsFacetsDD_location" class="optionsFacet-select"><option value="">Location</option><option>Omaha</option></select>
          <input type="submit" class="btn keywordsearch-button" value="Search Jobs" />
        </form>
        <h1>Sr AI Engineer</h1>
        <a class="btn btn-primary apply dialogApplyBtn" href="APPLY_HREF">Apply now »</a>
        <p>The posting, at length.</p>
        <form id="emailsubscribe" class="emailsubscribe-form form-inline" method="post" action="/talentcommunity/subscribe/">
          <label for="j_idt90">Select how often (in days) to receive an alert:</label>
          <input id="j_idt90" type="number" class="form-control subscribe-frequency" name="frequency" required min="1" max="99" value="7" />
          <input id="emailsubscribe-button" class="btn emailsubscribe-button" value="Create Alert" type="submit" />
        </form>
        </body></html>
        """;

    private const string SfSignInHtml = """
        <html><body>
        <form class="form-inline jobAlertsSearchForm" name="keywordsearch" method="get" action="/search/" role="search">
          <input type="text" class="keywordsearch-q" name="q" placeholder="Search by Keyword" aria-label="Search by Keyword" />
        </form>
        <h1>Career Opportunities: Sign In</h1>
        <p>Already have an account? Enter your email address and password (Credentials are case sensitive).</p>
        ERROR
        <form method="post" action="/jobs/sf/signin">
          <label for="username">Email Address:<span>*</span></label><input id="username" name="username" type="text" required />
          <label for="password">Password:<span>*</span></label><input id="password" name="password" type="password" required />
          <input type="button" id="show_button" value="Show" />
          <button type="submit">Sign In</button> <a href="/forgot">Forgot your password?</a>
        </form>
        <p>Not a registered user yet? <a href="/register">Create an account</a> to apply for our career opportunities.</p>
        </body></html>
        """;

    private const string SfApplicationHtml = """
        <html><body>
        <h1>Sr AI Engineer (181457)</h1>
        <p>WELCOME! Please ensure you complete your profile in its entirety.</p>
        <form id="app" method="post" action="/apply">
          <button type="button" id="50:topBar" aria-expanded="true" aria-controls="sec-docs">My Documents</button>
          <div id="sec-docs">
            <p>Accepted file types: DOCX, PDF, Image and Text</p>
            <div><label>* Upload Resume</label> <span>Required</span>
              <div><a href="#">Download document resume.pdf</a> <span>Last Modified: 07/09/2026</span> <span>resume.pdf (07/09/2026)</span> <a href="#">Edit document resume.pdf</a></div></div>
            <div><label>Cover letter</label> <div><a href="#">Download document Clark-Aaron-CoverLetter.pdf</a> <a href="#">Delete document</a></div></div>
          </div>
          <button type="button" id="117:topBar" aria-expanded="false" aria-controls="sec-profile">Profile Information</button>
          <div id="sec-profile" style="display:none">
            <label for="60:_txtFld">* Legal First Name</label><input id="60:_txtFld" name="firstName" value="Ada" required />
            <label for="68:_txtFld">* Legal Last Name</label><input id="68:_txtFld" name="lastName" value="Lovelace" required />
            <label for="72:_txtFld">* Primary Phone</label><input id="72:_txtFld" name="cellPhone" value="5712975406" required />
            <label for="80:_txtFld">* Email Address</label><input id="80:_txtFld" name="contactEmail" value="ada@example.com" required />
            <label for="lang">* Language Preference</label>
            <select id="lang" name="languagePreference" required><option value="">No Selection</option><option selected>English</option><option>French</option></select>
            <!-- SuccessFactors' paginated picklist, as Kiewit renders it (#222): a starred label
                 ("*&nbsp;Country"), a combobox input beside a button with the same aria-label,
                 the option list built only when the box is clicked, and a blur that empties any
                 text that is not one of the options. -->
            <label for="89:_input"><span aria-hidden="true">*</span>&nbsp;Country</label>
            <span class="sfCascadingPicklist"><input id="89:_input" name="country" aria-label="Country" role="combobox" aria-owns="90:_listSelect" aria-required="true" value="United States" autocomplete="off" placeholder="No Selection" />
            <button type="button" aria-label="Country" tabindex="-1" id="89:_selectButton"></button></span>
          </div>
          <button type="button" id="446:topBar" aria-expanded="false" aria-controls="sec-job">Job-Specific Information</button>
          <div id="sec-job" style="display:none">
            <label for="q2">* Are you legally authorized to work in the United States?</label>
            <select id="q2" name="q2" required><option value="">Select</option><option>Yes</option><option>No</option></select>
            <label for="q3">* Describe a system you scaled.</label>
            <textarea id="q3" name="q3" required></textarea>
            <label for="297:_input"><span aria-hidden="true">*</span>&nbsp;How did you hear about this opportunity?</label>
            <span class="sfCascadingPicklist"><input id="297:_input" name="hearAbout" aria-label="How did you hear about this opportunity?" role="combobox" aria-owns="298:_listSelect" aria-required="true" autocomplete="off" placeholder="No Selection" />
            <button type="button" aria-label="How did you hear about this opportunity?" tabindex="-1"></button></span>
            <label for="315:_input"><span aria-hidden="true">*</span>&nbsp;Have you ever worked for Kiewit or one if its subsidiary Companies?</label>
            <span class="sfCascadingPicklist"><input id="315:_input" name="workedBefore" aria-label="Have you ever worked for Kiewit or one if its subsidiary Companies?" role="combobox" aria-owns="316:_listSelect" aria-required="true" autocomplete="off" placeholder="No Selection" />
            <button type="button" aria-label="Have you ever worked for Kiewit or one if its subsidiary Companies?" tabindex="-1"></button></span>
            <!-- Two job-specific boxes the board gives the same name (#222). -->
            <label for="360:_txtFld"><span aria-hidden="true">*</span>&nbsp;Please list your number of years of professional software engineering experience</label>
            <input id="360:_txtFld" name="-1" type="text" aria-required="true" />
            <label for="364:_txtFld"><span aria-hidden="true">*</span>&nbsp;Please list your number of years of experience building production AI services, AI platforms, or reusable AI infrastructure</label>
            <input id="364:_txtFld" name="-1" type="text" aria-required="true" />
            <!-- The Acknowledgement: a label tied to nothing, a checkbox the widget re-renders on click. -->
            <div class="RCMFormField"><label for=""><span aria-hidden="true">*</span>&nbsp;Acknowledgement</label>
              <div><input type="checkbox" id="344:" name="app_ApplicantStatementConfirmation" aria-required="true" /> I certify that the information provided is true.</div></div>
          </div>
        </form>
        <div class="footer-bar"><span id="447:_backToListing" role="button" tabindex="0">View Profile</span> <span id="447:_saveBtn" role="button" tabindex="0">Save</span> <span id="447:_submitBtn" role="button" tabindex="0">Apply</span></div>
        <div id="confirm" role="dialog" aria-label="Confirm" style="display:none">
          <p>Are you sure you want to apply for this position?</p>
          <button type="button" id="confirm-no">Cancel</button> <button type="button" id="confirm-yes">Yes</button>
        </div>
        <script>
          const lists = {
            '90:_listSelect': ['No Selection', 'United States', 'Canada', 'Mexico'],
            '298:_listSelect': ['No Selection', 'Advertisement', 'College Career Fair/Event', 'Company Website', 'Employee Referral', 'Job Board', 'Kiewit Recruiter Contacted Me'],
            '316:_listSelect': ['No Selection', 'Yes', 'No'],
          };
          for (const input of document.querySelectorAll('input[role=combobox][aria-owns]')) {
            const owns = input.getAttribute('aria-owns');
            const open = () => {
              let ul = document.getElementById(owns);
              if (ul) return;
              ul = document.createElement('ul'); ul.id = owns; ul.setAttribute('role', 'listbox');
              for (const text of lists[owns]) {
                const li = document.createElement('li'); li.setAttribute('role', 'option');
                const a = document.createElement('a'); a.textContent = text; li.appendChild(a);
                li.addEventListener('mousedown', e => e.preventDefault());
                li.addEventListener('click', () => { input.value = text === 'No Selection' ? '' : text; input.title = input.value; ul.remove(); });
                ul.appendChild(li);
              }
              input.parentElement.appendChild(ul);
            };
            input.addEventListener('click', open);
            input.addEventListener('keydown', open);
            input.addEventListener('blur', () => { const ul = document.getElementById(owns); if (ul) ul.remove();
              if (!lists[owns].includes(input.value) || input.value === 'No Selection') { input.value = ''; input.classList.add('invalidInput'); } });
          }
          document.getElementById('344:').addEventListener('click', e => {
            // The widget swaps its box for a fresh one: the element that was clicked stays as it was.
            e.preventDefault();
            const old = e.target; const fresh = old.cloneNode(); fresh.checked = !old.checked; old.replaceWith(fresh);
            fresh.addEventListener('click', ev => { ev.preventDefault(); const f2 = fresh.cloneNode(); f2.checked = !fresh.checked; fresh.replaceWith(f2); });
          });
          for (const bar of document.querySelectorAll('[id$="topBar"]')) bar.addEventListener('click', () => {
            const open = bar.getAttribute('aria-expanded') === 'true';
            bar.setAttribute('aria-expanded', String(!open));
            document.getElementById(bar.getAttribute('aria-controls')).style.display = open ? 'none' : 'block';
          });
          document.getElementById('447:_submitBtn').addEventListener('click', () => { document.getElementById('confirm').style.display = 'block'; });
          document.getElementById('confirm-no').addEventListener('click', () => { document.getElementById('confirm').style.display = 'none'; });
          document.getElementById('confirm-yes').addEventListener('click', () => { document.getElementById('app').submit(); });
        </script>
        </body></html>
        """;

    private const string SearchFirstHtml = """
        <html><body>
        <header><input type="text" placeholder="Search jobs" aria-label="Search jobs" /></header>
        <h1>Senior Engineer</h1>
        <p>A posting page with no form on it.</p>
        <a href="/jobs/1">Apply now</a>
        </body></html>
        """;

    // A fixed, full-screen consent overlay with a "Manage" and an "Accept all" button; the
    // page underneath is BODY. Accepting removes the overlay, as the real managers do.
    private const string ConsentHtml = """
        <html><body>
        <div id="cookie-notice" role="dialog" aria-label="Cookie Consent"
             style="position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:1000;display:flex;align-items:flex-end">
          <div style="background:#fff;width:100%;padding:24px">
            <p>We use cookies to make this site work.</p>
            <button type="button" onclick="alert('manage')">Manage preferences</button>
            <button type="button" onclick="document.getElementById('cookie-notice').remove()">Accept all</button>
          </div>
        </div>
        BODY
        </body></html>
        """;

    // Nothing in the box is named for cookies — not the id, not the class, not the label.
    private const string UnnamedConsentHtml = """
        <html><body>
        <div id="system-imessage" role="dialog" aria-label="Important System Message"
             style="position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:1000;display:flex;align-items:flex-end">
          <div style="background:#fff;width:100%;padding:24px">
            <p>We use cookies and other tracking technologies to support navigation and improve our site.</p>
            <button type="button" id="BUTTONID" onclick="document.getElementById('system-imessage').remove()">Accept</button>
          </div>
        </div>
        BODY
        </body></html>
        """;

    // A full-screen bar with nothing to go on but where it sits and what it says.
    private const string PinnedLinkConsentHtml = """
        <html><body>
        <div id="notice-bar" style="position:fixed;inset:0;background:rgba(0,0,0,.6);z-index:1000;display:flex;align-items:flex-end">
          <div style="background:#fff;width:100%;padding:24px">
            <p>This website stores cookies on your device.</p>
            <a href="#" onclick="document.getElementById('notice-bar').remove();return false">Accept and continue</a>
          </div>
        </div>
        BODY
        </body></html>
        """;

    // A pre-application acknowledgement: a checkbox and "I agree", in a dialog whose small print
    // mentions cookies. Clicked as a banner, it would throw the application away.
    private const string AgreeBesideCheckboxHtml = """
        <html><body>
        <div id="ack-panel" role="dialog" aria-label="Before you apply"
             style="position:fixed;left:0;right:0;bottom:0;background:#fff;padding:16px">
          <p>This site uses cookies. Tick to confirm you have read the notice.</p>
          <label><input type="checkbox" name="ack"> I have read the applicant notice</label>
          <button type="button" onclick="document.body.innerHTML='<p>gone</p>'">I agree</button>
        </div>
        BODY
        </body></html>
        """;

    // The application sits inside a dialog whose small print mentions cookies. "I agree" here
    // throws the form away: clicking it as a banner would leave nothing to fill.
    private const string AgreeInsideFormDialogHtml = """
        <html><body>
        <div id="apply-panel" role="dialog" aria-label="Application">
          <p>By applying you acknowledge this site uses cookies.</p>
          <button type="button" onclick="document.getElementById('apply-panel').remove()">I agree</button>
          BODY
        </div>
        </body></html>
        """;

    // The dialog as LinkedIn ships it: `fb-dash-form-element` groups under data-test-form-element,
    // labels that repeat themselves for screen readers, a placeholder option that fails validation,
    // résumé cards and courtesy boxes that are not questions.
    private const string EasyApplyHtml = """
        <html><body>
        <h1>Senior .NET Engineer</h1>
        <!-- The search page's filter pill says "Easy Apply" too, and is not the way in. -->
        <button type="button" aria-label="Easy Apply filter." onclick="document.title='WRONG BUTTON'">Easy Apply</button>
        <!-- The posting's own page: an anchor with generated class names, known only by its aria-label. -->
        <a href="#" id="entry" class="f7a37dee _1d1ba039" aria-label="Easy Apply to Senior .NET Engineer at Acme">Easy Apply</a>
        <!-- ...and the dialog inside a shadow root, where document.querySelector cannot see it. -->
        <div id="interop-outlet"></div>
        <script>
        const steps = [
          `<h3>Contact info</h3>
           <div class="fb-dash-form-element" data-test-form-element><label for="c-email"><span aria-hidden="true">Email address</span><span class="visually-hidden">Email address</span></label>
             <select id="c-email" required aria-required="true"><option>Select an option</option><option selected>ada@example.com</option></select></div>
           <div class="fb-dash-form-element" data-test-form-element><label for="c-phone">Mobile phone number</label><input id="c-phone" type="text" required value="5550100"></div>`,
          `<h3>Resume</h3>
           <input type="radio" id="jobsDocumentCardToggle-1" checked><label for="jobsDocumentCardToggle-1">Deselect resume resume.pdf</label>
           <input type="file" id="jobs-document-upload-file-input-upload-resume">`,
          `<div class="fb-dash-form-element" data-test-form-element><input type="checkbox" id="top-choice"><label for="top-choice">Mark job as a top choice</label></div>`,
          `<h3>Additional Questions</h3>
           <div class="fb-dash-form-element" data-test-form-element><label for="q-auth"><span aria-hidden="true">Are you legally authorized to work in the United States?</span><span class="visually-hidden">Are you legally authorized to work in the United States?</span></label>
             <select id="q-auth" required aria-required="true"><option>Select an option</option><option>Yes</option><option>No</option></select></div>
           <div class="fb-dash-form-element" data-test-form-element><label for="q-years">How many years of experience do you have with C#?</label><input id="q-years" type="text" required></div>
           <fieldset data-test-form-builder-radio-button-form-component><legend><span aria-hidden="true">Will you require visa sponsorship?</span><span class="visually-hidden">Will you require visa sponsorship?</span></legend>
             <input type="radio" name="q-visa" id="q-visa-y" value="Yes" aria-required="true" style="position:absolute;opacity:0;width:0;height:0"><label for="q-visa-y">Yes</label>
             <input type="radio" name="q-visa" id="q-visa-n" value="No" aria-required="true" style="position:absolute;opacity:0;width:0;height:0"><label for="q-visa-n">No</label></fieldset>`,
          `<h3>Review your application</h3>
           <input type="checkbox" id="follow-company-checkbox" checked><label for="follow-company-checkbox">Follow Acme to stay up to date with their page.</label>`,
        ];
        const sr = document.getElementById('interop-outlet').attachShadow({ mode: 'open' });
        sr.innerHTML = '<div id="root"></div>';
        const rootEl = sr.getElementById('root');
        let at = 0; const answers = {};
        const send = o => fetch('/li/event', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(o) });
        function capture() { for (const e of sr.querySelectorAll('.jobs-easy-apply-modal select, .jobs-easy-apply-modal input[type=text]')) answers[e.id] = e.value;
          const v = sr.querySelector('input[name=q-visa]:checked'); if (v) answers.visa = v.value;
          const f = sr.getElementById('follow-company-checkbox'); if (f) answers.follow = f.checked; }
        function valid() { let ok = true;
          for (const g of sr.querySelectorAll('.jobs-easy-apply-modal [data-test-form-element], .jobs-easy-apply-modal fieldset')) {
            g.querySelector('.artdeco-inline-feedback--error')?.remove();
            const sel = g.querySelector('select[required]'), txt = g.querySelector('input[type=text][required]'), radios = g.querySelectorAll('input[type=radio]');
            const bad = (sel && /^select an option$/i.test(sel.options[sel.selectedIndex].text)) || (txt && (!txt.value.trim() || (txt.id === 'q-years' && !/^\d+(\.\d+)?$/.test(txt.value.trim()))))
              || (radios.length && ![...radios].some(r => r.checked));
            if (bad) { ok = false; const e = document.createElement('div'); e.className = 'artdeco-inline-feedback artdeco-inline-feedback--error'; e.textContent = 'Please enter a valid answer'; g.appendChild(e); } }
          return ok; }
        function render() {
          const last = at === steps.length - 1;
          rootEl.innerHTML = `<div class="artdeco-modal jobs-easy-apply-modal" role="dialog" data-test-modal>
            <button aria-label="Dismiss" data-test-modal-close-btn onclick="ask()">x</button>
            <progress max="100" value="${at * 25}"></progress>${steps[at]}
            <footer>${last ? '<button data-live-test-easy-apply-submit-button aria-label="Submit application" onclick="submitIt()">Submit application</button>'
              : `<button ${at === steps.length - 2 ? 'data-live-test-easy-apply-review-button aria-label="Review your application"' : 'data-easy-apply-next-button aria-label="Continue to next step"'} onclick="go()">
                   ${at === steps.length - 2 ? 'Review' : 'Next'}</button>`}</footer></div>`; }
        function go() { if (!valid()) return; capture(); at++; render(); }
        function ask() { capture(); rootEl.insertAdjacentHTML('beforeend',
          `<div role="alertdialog" class="artdeco-modal"><button data-test-dialog-secondary-btn onclick="leave('discard')">Discard</button><button data-test-dialog-primary-btn onclick="leave('save')">Save</button></div>`); }
        function leave(how) { send({ left: how, step: at }); rootEl.innerHTML = ''; }
        async function submitIt() { capture(); await fetch('/li/voyagerJobsDashOnsiteApplyApplication?action=submitApplication', { method: 'POST' }).catch(() => {});
          await send({ submitted: true, answers }); rootEl.innerHTML = '<div role="dialog" class="artdeco-modal"><h2>Application sent</h2><p>Your application was sent to Acme.</p><button>Done</button></div>'; }
        document.getElementById('entry').onclick = e => { e.preventDefault(); at = 0; render(); };
        </script>
        </body></html>
        """;

    private static AgentPacket EasyApplyPacket(bool answered)
    {
        var packet = new AgentPacket { ApplicationName = "acme-senior-.net-engineer.md", Provider = ApplyTrack.Api.Agent.AtsProvider.LinkedInEasy };
        packet.Questions.Add(new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard));
        packet.Answers["first_name"] = "Ada";
        if (!answered) return packet;
        // Keyed by label, as the driver hands a discovered question back.
        foreach (var (label, answer) in new[]
                 {
                     ("Are you legally authorized to work in the United States?", "Yes"),
                     ("How many years of experience do you have with C#?", "12"),
                     ("Will you require visa sponsorship?", "No"),
                 })
        {
            packet.Questions.Add(new(label, label, true, PacketQuestion.Select, [], PacketQuestion.Custom));
            packet.Answers[label] = answer;
        }
        return packet;
    }

    private List<System.Text.Json.JsonElement> EasyApplyEvents()
    {
        lock (_posts)
            return _posts.Where(p => p.ContainsKey("json")).Select(p => System.Text.Json.JsonDocument.Parse(p["json"]).RootElement.Clone()).ToList();
    }

    private const string ConsentPostingBody = """
        <h1>Senior Engineer</h1>
        <p>A posting page with no form on it, under a cookie banner.</p>
        <a href="/jobs/1">Apply now</a>
        """;

    // Zoho Recruit's career site as fyerx runs it: the posting loads clean, then a consent
    // web component arrives with a full-viewport freeze layer (z 100) under its banner
    // (z 110). Nothing on the page takes a click until Accept all or Decline is pressed —
    // the first dismissal, run straight after navigation, finds nothing to dismiss.
    private const string LateFreezeConsentHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <p>A posting page with no form on it; the cookie banner comes later.</p>
        <button type="button" onclick="location.href='/jobs/1'">I'm interested</button>
        <script>
          setTimeout(() => {
            const c = document.createElement('career-cookie-consent');
            c.innerHTML = '<div class="cw-cookie-freeze-layer" style="position:fixed;inset:0;z-index:100"></div>'
              + '<div class="cw-cookie-banner" style="position:fixed;left:0;right:0;bottom:0;z-index:110;background:#fff;padding:16px">'
              + '<p>We use cookies to improve your experience. See our <a href="#">Cookie Policy</a>.</p>'
              + '<a href="#">Manage cookies</a> '
              + '<button type="button" class="lyte-button primary cookie-accept-btn">Accept all</button> '
              + '<button type="button" class="lyte-button primary cookie-decline-btn">Decline non-essential</button></div>';
            document.body.appendChild(c);
            for (const b of c.querySelectorAll('button')) b.addEventListener('click', () => c.remove());
          }, 1500);
        </script>
        </body></html>
        """;

    private const string StuckApplyHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <p>A posting whose Apply never wakes up.</p>
        <button type="button" disabled>Apply</button>
        </body></html>
        """;

    // Paycor's recruitingbypaycor.com/SubmitResume: the page opens on a sponsorship question
    // with only radios and a Continue, and the form is behind it. Continue navigates to the
    // real form only once a choice is made — so a run that reaches the form proved it answered
    // the gate (#239).
    private const string PreScreenHtml = """
        <html><body>
        <h1>Senior Engineer</h1>
        <p>In order to apply for this position, please answer the questions below.</p>
        <form id="prescreen" onsubmit="return false">
          <fieldset><legend>Will you now or in the future require sponsorship for employment visa status?</legend>
            <label for="sp_yes">Yes</label><input id="sp_yes" type="radio" name="sponsorship" value="Yes" required />
            <label for="sp_no">No</label><input id="sp_no" type="radio" name="sponsorship" value="No" required />
          </fieldset>
          <button type="button" id="continue">Continue</button>
        </form>
        <script>
          document.getElementById('continue').addEventListener('click', () => {
            if (document.querySelector('input[name=sponsorship]:checked')) location.href = '/jobs/1';
          });
        </script>
        </body></html>
        """;

    /// <summary>The standard form packet plus the pre-screening sponsorship question, keyed by
    /// the gate radio's own name, as discovery banks it (#239).</summary>
    private static AgentPacket PreScreenPacket()
    {
        var packet = Packet();
        packet.Questions.Insert(0, new("sponsorship", "Will you now or in the future require sponsorship for employment visa status?",
            true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom));
        packet.Answers["sponsorship"] = "No";
        return packet;
    }

    private static AgentPacket ReactSelectPacket() => new()
    {
        ApplicationName = "acme-senior-engineer.md",
        Provider = "greenhouse",
        Questions =
        [
            new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            // What the Greenhouse API hands over: country is the synthetic optional question,
            // location is plain required text, the custom question is a select with options.
            new("country", "Country", false, PacketQuestion.Select, [], PacketQuestion.Standard),
            new("location", "Location", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("resume", "Resume/CV", true, PacketQuestion.File, [], PacketQuestion.Standard),
            new("question_9", "Are you willing to submit to a background check?", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom),
        ],
        Answers = new()
        {
            ["first_name"] = "Ada", ["country"] = "United States", ["location"] = "Omaha, NE", ["question_9"] = "Yes",
        },
    };

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

    /// <summary>A non-Greenhouse packet: the standard set every long-tail form gets.</summary>
    private static AgentPacket StandardPacket() => new()
    {
        ApplicationName = "acme-engineer.md", Provider = "unknown",
        Questions = PacketBuilder.StandardQuestions(),
        Answers = new() { ["std:first_name"] = "Ada", ["std:last_name"] = "Byte", ["std:email"] = "ada@example.com" },
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
    public async Task A_board_that_flags_the_application_as_spam_is_a_captcha_outcome_not_a_mystery()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md", Provider = "ashby",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/spam-on-submit", packet, null, dryRun: false);
        Assert.True(outcome.Captcha, outcome.Error);
        Assert.False(outcome.Submitted);
        // The earliest phrase on the page names the refusal; either of Ashby's will do.
        Assert.Contains("refused the application as a bot's", outcome.Error);
        Assert.Matches("couldn't submit your application|flagged as possible spam", outcome.Error);
        Assert.Contains("Copy answers and open", outcome.Error);
    }

    [SkippableFact]
    public async Task A_resume_field_called_cv_gets_the_resume()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-engineer.md", Provider = "unknown",
            Questions =
            [
                new("firstName", "First name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("lastName", "Last name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("email", "Email", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("cv", "Attach Resume", true, PacketQuestion.File, [], PacketQuestion.Standard),
            ],
            Answers = new() { ["firstName"] = "Ada", ["lastName"] = "Byte", ["email"] = "ada@example.com" },
        };
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/cv", packet, (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("cv", outcome.Mapped);
        var post = Assert.Single(_posts);
        Assert.Equal($"resume.pdf:{Pdf.Length}", post["resume:file"]);
    }

    [SkippableFact]
    public async Task A_form_loaded_into_an_iframe_on_apply_is_found_filled_and_submitted()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var dry = await Submitter().RunAsync($"{_fixtureUrl}/jobs/framed", Packet(), (Pdf, "resume.pdf"), dryRun: true);
        Assert.True(dry.Filled, dry.Error);
        Assert.Equal(6, dry.Mapped.Count);
        Assert.Empty(dry.Unmapped);
        Assert.Empty(_posts);

        var real = await Submitter().RunAsync($"{_fixtureUrl}/jobs/framed", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(real.Submitted, real.Error);
        Assert.Contains("Thank you", real.Confirmation);
        var post = Assert.Single(_posts);
        Assert.Equal("Ada", post["job_application[first_name]"]);
        Assert.Equal($"resume.pdf:{Pdf.Length}", post["resume:file"]);
    }

    [SkippableFact]
    public async Task A_form_fetched_after_load_with_one_name_box_gets_first_and_last_together()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/late-name", StandardPacket(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        var post = Assert.Single(_posts);
        Assert.Equal("Ada Byte", post["_systemfield_name"]);
        Assert.Equal("ada@example.com", post["_systemfield_email"]);
        Assert.Contains("std:first_name", outcome.Mapped);
        Assert.Contains("std:last_name", outcome.Mapped);
        Assert.Empty(outcome.Unmapped);
    }

    [SkippableFact]
    public async Task A_form_whose_labels_are_only_text_above_the_boxes_still_maps()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/text-labels", StandardPacket(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        var post = Assert.Single(_posts);
        Assert.Equal("Ada", post["f_1"]);
        Assert.Equal("Byte", post["f_2"]);
        Assert.Equal("ada@example.com", post["f_3"]);
    }

    [SkippableFact]
    public async Task A_page_with_no_form_is_a_failed_dry_run_not_a_clean_one()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/blank", Packet(), (Pdf, "resume.pdf"), dryRun: true);
        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Empty(outcome.Mapped);
        Assert.Contains("no application form was found", outcome.Error);
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
    public async Task A_confirmation_that_arrives_seconds_after_the_click_is_waited_for_and_the_post_is_recorded()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md", Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/slow", packet, null, dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("Thank you", outcome.Confirmation);
        Assert.Contains("\"first_name\":\"Ada\"", Assert.Single(_posts)["json"]);
    }

    [SkippableFact]
    public async Task A_security_code_the_board_emails_is_relayed_by_the_human_and_the_run_finishes_in_the_same_session()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md", Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };
        var askedFor = "";
        Task<string?> Relay(CodeRequest ask, CancellationToken _) { askedFor = ask.Recipient; Assert.False(ask.SignIn); return Task.FromResult<string?>("ieg0pxwr"); }

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/security-code", packet, null, dryRun: false, awaitSecurityCode: Relay);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("ada@example.com", askedFor);
        Assert.Contains("\"security_code\":\"IEG0PXWR\"", Assert.Single(_posts)["json"]);
    }

    [SkippableFact]
    public async Task A_security_code_that_never_arrives_ends_the_run_with_the_recipient_named_and_nothing_submitted()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md", Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/security-code", packet, null, dryRun: false,
            awaitSecurityCode: (_, _) => Task.FromResult<string?>(null));

        Assert.False(outcome.Submitted);
        Assert.False(outcome.Captcha);
        Assert.Contains("security code to ada@example.com", outcome.Error);
        Assert.Contains("run Submit again", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task An_error_the_form_shows_seconds_after_the_click_is_the_reported_reason_with_what_was_sent()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md", Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/slow-error", packet, null, dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("There was an error processing your application", outcome.Error);
        Assert.Contains("no application request was sent", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task The_submit_button_is_the_forms_not_the_apply_anchor_above_the_posting()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Every production real run clicked the "Apply" anchor — first in document order —
        // and reported no confirmation while no request ever left the page.
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md", Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/anchor", packet, null, dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
    }

    [SkippableFact]
    public async Task Apply_later_is_never_the_form_nor_the_button_the_real_form_behind_Apply_is()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "piston-founding-engineer.md", Provider = "join",
            Questions =
            [
                new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("email", "Email", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            ],
            Answers = new() { ["first_name"] = "Ada", ["email"] = "ada@example.com" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/apply-later", packet, null, dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        var sent = Assert.Single(_posts);
        Assert.Equal("Ada", sent["job_application[first_name]"]);
        Assert.Equal("ada@example.com", sent["job_application[email]"]);
        Assert.Empty(_laterPosts);
    }

    [SkippableFact]
    public async Task An_email_sign_in_behind_Apply_is_reported_as_such_not_as_a_clean_dry_run()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "piston-founding-engineer.md", Provider = "join",
            Questions = [new("email", "Email", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["email"] = "ada@example.com" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sign-in", packet, null, dryRun: true);

        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Contains("sign-in", outcome.Error);
        Assert.Contains("signs in as ada@example.com", outcome.Error);
        Assert.Empty(_posts);
    }

    private static AgentPacket JoinPacket() => new()
    {
        ApplicationName = "piston-founding-engineer.md", Provider = "join",
        Questions =
        [
            new("std:first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("std:last_name", "Last Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("std:email", "Email", true, PacketQuestion.Text, [], PacketQuestion.Standard),
        ],
        Answers = new() { ["std:first_name"] = "Ada", ["std:last_name"] = "Lovelace", ["std:email"] = "ada@example.com" },
    };

    [SkippableFact]
    public async Task A_sign_in_code_the_board_emails_is_relayed_and_the_run_goes_on_to_the_form_behind_it()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        CodeRequest? asked = null;
        Task<string?> Relay(CodeRequest ask, CancellationToken _) { asked = ask; return Task.FromResult<string?>(" 482913 "); }

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sign-in-code", JoinPacket(), null, dryRun: false, awaitSecurityCode: Relay);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.NotNull(asked);
        Assert.True(asked!.SignIn);
        Assert.Equal("ada@example.com", asked.Recipient);
        var sent = Assert.Single(_posts);
        Assert.Equal("Ada", sent["job_application[first_name]"]);
        Assert.Equal("Lovelace", sent["job_application[last_name]"]);
        Assert.Equal("ada@example.com", sent["job_application[email]"]);
        Assert.Empty(_laterPosts);
    }

    [SkippableFact]
    public async Task A_sign_in_link_the_board_emails_is_opened_in_the_same_session_and_the_form_behind_it_is_submitted()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        CodeRequest? asked = null;
        Task<string?> Relay(CodeRequest ask, CancellationToken _) { asked = ask; return Task.FromResult<string?>($"{_fixtureUrl}/jobs/sign-in-link/verify?token=abc123"); }

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sign-in-link", JoinPacket(), null, dryRun: false, awaitSecurityCode: Relay);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.True(asked!.SignIn);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
        Assert.Empty(_laterPosts);
    }

    [SkippableFact]
    public async Task A_relayed_sign_in_link_off_the_boards_site_is_never_followed()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sign-in-link", JoinPacket(), null, dryRun: false,
            awaitSecurityCode: (_, _) => Task.FromResult<string?>("https://evil.example/steal?token=abc123"));

        Assert.False(outcome.Submitted);
        Assert.False(outcome.Filled);
        Assert.Contains("off the board's site", outcome.Error);
        Assert.Contains("evil.example", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_sign_in_nobody_relays_ends_the_run_naming_the_address_with_nothing_submitted()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sign-in-code", JoinPacket(), null, dryRun: false,
            awaitSecurityCode: (_, _) => Task.FromResult<string?>(null));

        Assert.False(outcome.Submitted);
        Assert.Contains("sign-in code to ada@example.com", outcome.Error);
        Assert.Contains("run Submit again", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_posting_taken_down_to_a_404_page_is_reported_closed()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/gone", Packet(), (Pdf, "resume.pdf"), dryRun: true);

        Assert.True(outcome.Closed);
        Assert.False(outcome.Filled);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_required_cover_letter_file_is_entered_as_text_in_its_own_box_not_the_resumes()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = OnePlusResumePacket();
        packet.Questions.Add(new("cover_letter", "Cover Letter", true, PacketQuestion.File, [], PacketQuestion.Standard));
        const string letter = "Dear Acme, I would like to scale your billing.";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/cover", packet, (Pdf, "resume.pdf"), dryRun: false, coverLetter: letter);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("cover_letter", outcome.Mapped);
        var post = Assert.Single(_posts);
        Assert.Equal(letter, post["cover_letter_text"]);
        Assert.Equal("", post["resume_text"]);            // the résumé's box was left alone
        Assert.Equal($"resume.pdf:{Pdf.Length}", post["resume:file"]);
    }

    [SkippableFact]
    public async Task A_required_cover_letter_file_with_no_letter_drafted_stays_unmapped()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = OnePlusResumePacket();
        packet.Questions.Add(new("cover_letter", "Cover Letter", true, PacketQuestion.File, [], PacketQuestion.Standard));

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/cover", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Equal(["cover_letter"], outcome.Unmapped);
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
    public async Task Easy_apply_hands_back_the_questions_it_meets_for_the_first_time_and_saves_the_draft()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // The employer's questions do not exist until their step is reached, so no discovery pass
        // can know them. The run walks as far as it can, names what it found, and keeps LinkedIn's
        // own draft so the person can finish from there (#278).
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/li/jobs/view/1", EasyApplyPacket(answered: false), (Pdf, "resume.pdf"), dryRun: true);

        Assert.False(outcome.Submitted);
        Assert.Equal(
            ["Are you legally authorized to work in the United States?", "How many years of experience do you have with C#?", "Will you require visa sponsorship?"],
            outcome.Unmapped);
        var found = Assert.IsType<List<PacketQuestion>>(outcome.Discovered);
        Assert.Equal(outcome.Unmapped, found.Select(q => q.Id).ToList());
        Assert.Equal(["Yes", "No"], found[0].Options);
        Assert.Equal(PacketQuestion.Text, found[1].Type);
        Assert.Equal(["Yes", "No"], found[2].Options);
        Assert.All(found, q => Assert.True(q.Required));
        var left = Assert.Single(EasyApplyEvents());
        Assert.Equal("save", left.GetProperty("left").GetString());
        Assert.Equal(3, left.GetProperty("step").GetInt32());
    }

    [SkippableFact]
    public async Task Easy_apply_dry_run_walks_to_the_submit_step_and_discards_without_sending()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/li/jobs/view/1", EasyApplyPacket(answered: true), (Pdf, "resume.pdf"), dryRun: true);

        Assert.True(outcome.Filled, outcome.Error);
        Assert.False(outcome.Submitted);
        Assert.Empty(outcome.Unmapped);
        Assert.Equal(3, outcome.Mapped.Count);
        Assert.NotNull(outcome.Screenshot);
        var left = Assert.Single(EasyApplyEvents());
        Assert.Equal("discard", left.GetProperty("left").GetString());
    }

    [SkippableFact]
    public async Task Easy_apply_submits_with_the_packets_answers_and_without_following_the_company()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/li/jobs/view/1", EasyApplyPacket(answered: true), (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("Application sent", outcome.Confirmation);
        var sent = Assert.Single(EasyApplyEvents()).GetProperty("answers");
        Assert.Equal("Yes", sent.GetProperty("q-auth").GetString());
        Assert.Equal("12", sent.GetProperty("q-years").GetString());
        Assert.Equal("No", sent.GetProperty("visa").GetString());
        // "Follow Acme" arrives ticked. Applying is not following.
        Assert.False(sent.GetProperty("follow").GetBoolean());
    }

    [SkippableFact]
    public async Task Easy_apply_refuses_to_submit_while_it_would_follow_the_company_in_your_name()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/li/jobs/view/sticky-follow", EasyApplyPacket(answered: true), (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("could not be unticked", outcome.Error);
        var events = EasyApplyEvents();
        Assert.DoesNotContain(events, e => e.TryGetProperty("submitted", out _));
        Assert.Equal("save", Assert.Single(events).GetProperty("left").GetString());
    }

    [SkippableFact]
    public async Task Easy_apply_stops_at_a_sign_in_instead_of_pressing_on()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/li/login", EasyApplyPacket(answered: true), (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("LinkedIn asked to sign in", outcome.Error);
        Assert.Empty(EasyApplyEvents());
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
    public async Task A_checkbox_hidden_behind_a_drawn_box_is_set_through_its_label_not_timed_out_on()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // gravie on Ashby: "TimeoutException: Timeout 20000ms exceeded … element is not visible",
        // four runs running, and the whole run lost to one box (#280).
        var packet = Packet();
        packet.Questions.Add(new("job_application[question_7]", "I accept the privacy policy", true,
            PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom));
        packet.Answers["job_application[question_7]"] = "Yes";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/styled-checkbox", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("job_application[question_7]", outcome.Mapped);
        Assert.Equal("accepted", Assert.Single(_posts)["job_application[question_7]"]);
    }

    [SkippableTheory]
    [InlineData("No", "no")]
    [InlineData("Yes", "yes")]
    public async Task A_yes_no_button_pair_is_pressed_not_read_off_the_hidden_checkbox_that_mirrors_it(string answer, string posted)
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // "No" is the case that lied: the hidden box was already unchecked, so nothing was pressed,
        // the dry run came back clean, and the real submit was rejected as unanswered (#280).
        var packet = Packet();
        packet.Questions.Add(new("visa", "Will you now, or in the future, require employer visa sponsorship?", true,
            PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom));
        packet.Answers["visa"] = answer;

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/ashby-yesno", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("visa", outcome.Mapped);
        Assert.Equal(posted, Assert.Single(_posts)["visa_answer"]);
    }

    [SkippableFact]
    public async Task An_apply_link_whose_accessible_name_opens_with_the_job_title_is_still_found()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/apply-named-for-the-job", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
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

    /// <summary>What Greenhouse's Job Board API hands over for GitLab: a résumé it calls optional.</summary>
    private static AgentPacket OptionalResumePacket() => new()
    {
        ApplicationName = "acme-senior-engineer.md",
        Provider = "greenhouse",
        Questions =
        [
            new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
            new("resume", "Resume/CV", false, PacketQuestion.File, [], PacketQuestion.Standard),
        ],
        Answers = new() { ["first_name"] = "Ada" },
    };

    [SkippableFact]
    public async Task A_resume_the_api_called_optional_whose_uploader_fails_late_goes_in_as_text_not_into_a_rejection()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // #195: the input held the file and the page was quiet for over a second before the
        // uploader threw. The old 1.5 s look believed the attach; the form then said
        // "Resume/CV is required".
        const string text = "Ada Byte — Staff Engineer. Scaled billing to 10x.";
        var outcome = await Submitter().RunAsync(
            $"{_fixtureUrl}/jobs/gh-resume?err=2500", OptionalResumePacket(), (Pdf, "resume.pdf"), dryRun: false, resumeText: text);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("resume", outcome.Mapped);
        Assert.Empty(outcome.Unmapped);
        var post = Assert.Single(_posts);
        Assert.Equal(text, post["resume_text"]);
        Assert.DoesNotContain("resume.pdf", post["resume:file"]);
    }

    [SkippableFact]
    public async Task A_resume_the_api_called_optional_that_would_not_attach_refuses_the_click_when_there_is_no_text()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // The API's "optional" is not the form's: with a PDF on hand a failed attach is
        // unmapped, never a click into the form's own validation.
        var outcome = await Submitter().RunAsync(
            $"{_fixtureUrl}/jobs/gh-resume?err=2500", OptionalResumePacket(), (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("resume", outcome.Unmapped);
        Assert.Contains("could not be mapped", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task An_uploader_that_fails_after_the_attach_was_believed_is_caught_by_the_forms_own_required_and_retried_as_text()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // The failure lands after the attach has been believed and while the cover letter is
        // being entered. The pre-click sweep now reads the upload group's own required flag,
        // sees the error where the file should be, and takes the Enter manually route
        // before judging — instead of clicking Submit with no résumé on the form.
        var packet = OptionalResumePacket();
        packet.Questions.Add(new("cover_letter", "Cover Letter", false, PacketQuestion.File, [], PacketQuestion.Standard));
        const string text = "Ada Byte — Staff Engineer.";
        const string letter = "Dear Acme, I would like to scale your billing.";
        var outcome = await Submitter().RunAsync(
            $"{_fixtureUrl}/jobs/gh-resume?err=3800", packet, (Pdf, "resume.pdf"), dryRun: false, resumeText: text, coverLetter: letter);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Contains("resume", outcome.Mapped);
        Assert.Contains("cover_letter", outcome.Mapped);
        var post = Assert.Single(_posts);
        Assert.Equal(text, post["resume_text"]);
        Assert.Equal(letter, post["cover_letter_text"]);
        Assert.DoesNotContain("resume.pdf", post["resume:file"]);
    }

    [SkippableFact]
    public async Task An_uploader_that_fails_after_the_attach_was_believed_refuses_the_click_when_there_is_no_text()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = OptionalResumePacket();
        packet.Questions.Add(new("cover_letter", "Cover Letter", false, PacketQuestion.File, [], PacketQuestion.Standard));
        var outcome = await Submitter().RunAsync(
            $"{_fixtureUrl}/jobs/gh-resume?err=3800", packet, (Pdf, "resume.pdf"), dryRun: false, coverLetter: "Dear Acme.");

        Assert.False(outcome.Submitted);
        Assert.Contains("resume", outcome.Unmapped);
        Assert.DoesNotContain("resume", outcome.Mapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_required_upload_group_the_packet_never_listed_is_unmapped_by_the_forms_own_required()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Like the Country picker: the rendered form wants a résumé the API never mentioned.
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md", Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/gh-resume", packet, null, dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Equal(["resume"], outcome.Unmapped);
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

    [SkippableFact]
    public async Task A_react_select_combobox_is_driven_to_a_committed_option_and_reaches_the_post()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Country: the option is "United States +1", the answer "United States" — chosen by
        // prefix, never by exact text. City: an async geocoder that suggests for "Omaha, NE";
        // the first suggestion is taken. Custom question: a fixed Yes/No set.
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/react-select", ReactSelectPacket(), (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Empty(outcome.Unmapped);
        Assert.Equal(["first_name", "country", "location", "resume", "question_9"], outcome.Mapped);
        var post = Assert.Single(_posts);
        Assert.Equal("US", post["country_value"]);
        Assert.Equal("Omaha, Nebraska, United States", post["location_value"]);
        Assert.Equal("1", post["question_9_value"]);
    }

    [SkippableFact]
    public async Task An_autocomplete_that_finds_nothing_for_the_full_text_is_retried_with_a_shorter_one()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // The geocoder has nothing for "Omaha, NE (Remote)" and a suggestion for "Omaha, NE".
        var packet = ReactSelectPacket();
        packet.Answers["location"] = "Omaha, NE (Remote)";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/react-select", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Omaha, Nebraska, United States", Assert.Single(_posts)["location_value"]);
    }

    [SkippableFact]
    public async Task A_rendered_field_with_the_forms_own_prefix_is_reported_as_the_packets_question()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Nothing the geocoder can place: the packet's `location` stays empty. The rendered
        // field is `candidate-location`; it must be reported once, as `location`, not twice.
        var packet = ReactSelectPacket();
        packet.Answers["location"] = "Nowhere";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/react-select", packet, (Pdf, "resume.pdf"), dryRun: true);

        Assert.Equal(["location"], outcome.Unmapped);
        Assert.DoesNotContain("location", outcome.Mapped);
    }

    [SkippableFact]
    public async Task A_required_field_the_packet_never_knew_is_reported_unmapped_and_refuses_the_click()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // The production failure: Greenhouse's API lists no country question, its form
        // requires one, and the run reported every packet question mapped and clicked into
        // "Select a country". The rendered form's own required state is the last word.
        var packet = ReactSelectPacket();
        packet.Questions.RemoveAll(q => q.Id == "country");
        packet.Answers.Remove("country");

        var dry = await Submitter().RunAsync($"{_fixtureUrl}/jobs/react-select", packet, (Pdf, "resume.pdf"), dryRun: true);
        Assert.Equal(["country"], dry.Unmapped);
        Assert.Equal("", dry.Error);

        var real = await Submitter().RunAsync($"{_fixtureUrl}/jobs/react-select", packet, (Pdf, "resume.pdf"), dryRun: false);
        Assert.False(real.Submitted);
        Assert.Equal(["country"], real.Unmapped);
        Assert.Contains("could not be mapped", real.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_combobox_answer_that_commits_nothing_is_not_counted_as_mapped()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // No option starts with or contains "Mars"; the widget keeps the text as search input
        // and commits nothing — which used to count as mapped.
        var packet = ReactSelectPacket();
        packet.Answers["country"] = "Mars";

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/react-select", packet, (Pdf, "resume.pdf"), dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("country", outcome.Unmapped);
        Assert.DoesNotContain("country", outcome.Mapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_form_where_nothing_maps_is_refused_not_submitted_empty()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Zero mapped, zero unmapped — a packet for some other form. Seen in production, and
        // the old code would have clicked Submit on an empty application.
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md",
            Provider = "greenhouse",
            Questions = [new("zz_given", "Given name", false, PacketQuestion.Text, [], PacketQuestion.Custom)],
            Answers = new() { ["zz_given"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/1", packet, null, dryRun: false);

        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Contains("nothing on this form could be filled", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_submit_the_forms_own_validation_rejects_reports_the_forms_message()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md",
            Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/validating", packet, null, dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("the form rejected the submission", outcome.Error);
        Assert.Contains("Email is required.", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_form_that_resets_itself_on_submit_is_reported_as_such()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var packet = new AgentPacket
        {
            ApplicationName = "acme-senior-engineer.md",
            Provider = "greenhouse",
            Questions = [new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard)],
            Answers = new() { ["first_name"] = "Ada" },
        };

        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/resetting", packet, null, dryRun: false);

        Assert.False(outcome.Submitted);
        Assert.Contains("reset itself", outcome.Error);
        Assert.Empty(_posts);
    }

    /// <summary>
    /// The live check. Set <c>APPLYTRACK_LIVE_GREENHOUSE_URL</c> to a real Greenhouse posting
    /// and this drives the actual form: the packet comes from the real Job Board API, every
    /// question gets a stock answer, the résumé is a stand-in PDF, and <b>every non-GET request
    /// is aborted at the browser</b>, so Submit is clicked and nothing can ever be sent. Two
    /// assertions: the dry run maps every required field the form renders, and the click meets
    /// no "is required" wall. Never runs in CI.
    /// </summary>
    [SkippableFact]
    public async Task Live_greenhouse_form_fills_every_required_field_and_the_click_meets_no_validation_wall()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var url = Environment.GetEnvironmentVariable("APPLYTRACK_LIVE_GREENHOUSE_URL") ?? "";
        Skip.If(url.Length == 0, "set APPLYTRACK_LIVE_GREENHOUSE_URL to a Greenhouse posting to run this");
        Assert.True(ApplyTrack.Api.Agent.AtsProvider.TryParseGreenhouse(url, "", out var board, out var jobId), "not a Greenhouse URL");

        var http = new HttpClient(new SocketsHttpHandler()) { BaseAddress = new Uri("https://boards-api.greenhouse.io/") };
        var job = await new ApplyTrack.Api.Agent.Greenhouse.GreenhouseBoard(
                new PassThroughFactory(http), NullLogger<ApplyTrack.Api.Agent.Greenhouse.GreenhouseBoard>.Instance)
            .GetJobAsync(board, jobId);
        Assert.NotNull(job);
        var packet = new AgentPacket { ApplicationName = "live.md", Provider = "greenhouse", Questions = job!.Questions };
        var ctx = new ApplyTrack.Api.Agent.AnswerContext(
            new Resume { FullName = "Ada Byte", Location = "Omaha, NE", Links = [new ResumeLink("LinkedIn", "https://linkedin.com/in/ada")] },
            new AgentSettings { Phone = "4025550100", SalaryExpectation = "150000", WorkAuthorization = "US citizen" },
            "ada@example.com", "", "");
        foreach (var q in packet.Questions.Where(q => q.Kind != PacketQuestion.Eeo && q.Type != PacketQuestion.File))
        {
            var (answer, _) = ApplyTrack.Api.Agent.AnswerDrafter.Deterministic(q, ctx);
            packet.Answers[q.Id] = answer ?? (q.Options.Count > 0 ? q.Options[0] : q.Type == PacketQuestion.Textarea ? "Stand-in text." : "n/a");
        }

        var submitter = new BrowserSubmitter(
            new BrowserOptions { Endpoint = _ws, TimeoutSeconds = 120, BlockSubmissions = true },
            NullLogger<BrowserSubmitter>.Instance);
        var dry = await submitter.RunAsync(url, packet, (Pdf, "resume.pdf"), dryRun: true, resumeText: "Ada Byte. Ships .NET.");
        Console.WriteLine($"LIVE DRY: mapped=[{string.Join(", ", dry.Mapped)}] unmapped=[{string.Join(", ", dry.Unmapped)}] error={dry.Error}");
        Assert.True(dry.Filled, dry.Error);
        Assert.Empty(dry.Unmapped);

        var real = await submitter.RunAsync(url, packet, (Pdf, "resume.pdf"), dryRun: false, resumeText: "Ada Byte. Ships .NET.");
        Console.WriteLine($"LIVE REAL: submitted={real.Submitted} unmapped=[{string.Join(", ", real.Unmapped)}] error={real.Error}");
        Assert.False(real.Submitted); // the POST was aborted; a confirmation here would mean the block failed
        Assert.DoesNotMatch("(?i)is required|select a country|enter your location", real.Error);
    }

    private sealed class PassThroughFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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

    [SkippableFact]
    public async Task An_exact_label_wins_over_a_longer_one_that_merely_contains_it()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // "LinkedIn profile" (required) used to match "LinkedIn Profile URL" first, so the URL
        // box was filled twice and the required one stayed empty — the run stopped on it (#187).
        var packet = new AgentPacket
        {
            ApplicationName = "kendago-engineer.md", Provider = "unknown",
            Questions =
            [
                new("first_name", "First Name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("li_url", "LinkedIn Profile URL", false, PacketQuestion.Text, [], PacketQuestion.Standard),
                new("LinkedIn profile", "LinkedIn profile", true, PacketQuestion.Text, [], PacketQuestion.Custom),
            ],
            Answers = new()
            {
                ["first_name"] = "Ada", ["li_url"] = "https://www.linkedin.com/in/ada", ["LinkedIn profile"] = "https://www.linkedin.com/in/ada",
            },
        };
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/two-linkedin", packet, null, dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        var post = Assert.Single(_posts);
        Assert.Equal("https://www.linkedin.com/in/ada", post["li_url"]);
        Assert.Equal("https://www.linkedin.com/in/ada", post["li_req"]);
    }

    [SkippableFact]
    public async Task A_search_box_on_the_posting_page_is_not_the_form_and_apply_is_followed_to_it()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/search-first", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.EndsWith("/apply", new Uri(outcome.Url).AbsolutePath);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
    }

    [SkippableFact]
    public async Task A_job_search_and_a_job_alert_box_are_not_the_form_and_apply_now_is_followed_to_it()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/widgets", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
        Assert.DoesNotContain("frequency", outcome.Unmapped);
        Assert.DoesNotContain("locationsearch", outcome.Mapped);
    }

    [SkippableFact]
    public async Task Apply_that_leads_to_an_account_only_ats_is_named_as_such_and_nothing_is_filled()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/widgets-account", Packet(), null, dryRun: true);
        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Contains("SAP SuccessFactors", outcome.Error);
        Assert.Contains("signed-in candidate account", outcome.Error);
        Assert.Empty(outcome.Mapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task Apply_that_leads_to_a_social_sign_up_wall_names_the_providers_and_nothing_is_filled()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/widgets-sso", Packet(), null, dryRun: true);
        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Contains("only signs candidates up through Google, LinkedIn", outcome.Error);
        Assert.Contains("Copy answers and open", outcome.Error);
        Assert.DoesNotContain("no form appeared", outcome.Error);
        Assert.Empty(outcome.Mapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_sign_up_wall_that_opens_in_a_new_tab_after_a_pause_is_still_named()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/widgets-sso-late", Packet(), null, dryRun: true);
        Assert.False(outcome.Filled);
        Assert.Contains("only signs candidates up through Google, LinkedIn", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task An_apply_button_that_opens_a_menu_is_followed_through_its_apply_item()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/menu-apply", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
    }

    private const string MenuApplyHtml = """
        <html><body>
        <h1>.Net Developer</h1>
        <div>
          <button type="button" aria-haspopup="true" aria-expanded="false"
            onclick="this.setAttribute('aria-expanded', 'true'); document.getElementById('apply-menu').hidden = false">Apply Now</button>
          <ul id="apply-menu" role="menu" hidden>
            <li role="menuitem" tabindex="0" onclick="location = '/jobs/1'">Apply Now</li>
            <li role="menuitem" tabindex="0" onclick="location = '/jobs/nowhere'">Start applying with LinkedIn</li>
          </ul>
        </div>
        <p>The posting, at length.</p>
        </body></html>
        """;

    private const string SsoWallHtml = """
        <html><body>
        <h1>Get paid to align groundbreaking AI models with your expertise</h1>
        <label><input type="checkbox" /> I agree to the Terms of Service, Privacy Policy and Release Agreement</label>
        <button type="button">Continue with Google</button>
        <button type="button">Continue with LinkedIn</button>
        </body></html>
        """;

    [SkippableFact]
    public async Task A_job_search_and_a_job_alert_box_are_not_discovered_as_questions()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var discoverer = new FormDiscoverer(new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true, TimeoutSeconds = 60 }, NullLogger<FormDiscoverer>.Instance);
        var questions = await discoverer.DiscoverAsync($"{_fixtureUrl}/jobs/widgets");
        Assert.NotNull(questions);
        Assert.Contains(questions!, q => q.Label.Contains("First Name", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(questions!, q => q.Id == "q" || q.Id == "locationsearch" || q.Id == "j_idt90" || q.Label.Contains("alert", StringComparison.OrdinalIgnoreCase));
        Assert.Null(await discoverer.DiscoverAsync($"{_fixtureUrl}/jobs/widgets-account"));
    }

    private static readonly BoardAccount[] SfAccount = [new("127.0.0.1", "ada@example.com", "Sekrit-9$")];

    /// <summary>The standard packet plus two questions the account already answered on the form,
    /// with the packet holding a different guess for each (#216).</summary>
    private static AgentPacket SfPacket()
    {
        var p = Packet();
        p.Questions.Add(new("111:_input", "Language Preference", true, PacketQuestion.Text, [], PacketQuestion.Custom));
        p.Questions.Add(new("89:_input", "Country", true, PacketQuestion.Text, [], PacketQuestion.Standard));
        p.Answers["111:_input"] = "Spanish";
        p.Answers["89:_input"] = "Mexico";
        // Discovered on an earlier render, when the board numbered these 301 and 319; the
        // page now says 297 and 315. Labels as discovery stored them, star and all (#222).
        p.Questions.Add(new("301:_input", "*\n How did you hear about this opportunity?", true, PacketQuestion.Text, [], PacketQuestion.Custom));
        p.Questions.Add(new("319:_input", "*\n Have you ever worked for Kiewit or one if its subsidiary Companies?", true, PacketQuestion.Text, [], PacketQuestion.Custom));
        p.Questions.Add(new("-1", "*\n Please list your number of years of professional software engineering experience", true, PacketQuestion.Text, [], PacketQuestion.Custom));
        p.Questions.Add(new("*\n Please list your number of years of experience building production AI services, AI platforms, or reusable AI infrastructure",
            "*\n Please list your number of years of experience building production AI services, AI platforms, or reusable AI infrastructure", true, PacketQuestion.Text, [], PacketQuestion.Custom));
        p.Questions.Add(new("app_ApplicantStatementConfirmation", "*\n Acknowledgement", true, PacketQuestion.Select, ["Yes", "No"], PacketQuestion.Custom));
        p.Answers["301:_input"] = "Job Board";
        p.Answers["319:_input"] = "No";
        p.Answers["-1"] = "25";
        p.Answers["*\n Please list your number of years of experience building production AI services, AI platforms, or reusable AI infrastructure"] = "1";
        p.Answers["app_ApplicantStatementConfirmation"] = "Yes";
        return p;
    }

    [SkippableFact]
    public async Task With_a_saved_board_account_the_browser_signs_in_opens_the_folded_sections_and_applies()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sf", SfPacket(), (Pdf, "resume.pdf"), dryRun: false, accounts: SfAccount);
        Assert.True(outcome.Submitted, outcome.Error);
        var sent = Assert.Single(_posts);
        Assert.Equal("Ada", sent["firstName"]);
        // The pickers the account had already filled were left as they were, and counted.
        Assert.Equal("English", sent["languagePreference"]);
        Assert.Contains("111:_input", outcome.Mapped);
        Assert.Contains("89:_input", outcome.Mapped);
        Assert.Equal("Yes", sent["q2"]);
        Assert.False(string.IsNullOrWhiteSpace(sent["q3"]));
        // The résumé the account already holds counted as attached; nothing was uploaded.
        Assert.Contains("resume", outcome.Mapped);
        Assert.Equal("", sent["resume:file"]);
        // The pickers discovered under other numbers were found by their labels and the pinned
        // answers chosen from the menus; the Acknowledgement box is checked though the widget
        // swapped it under the click; the two boxes named "-1" each got their own answer (#222).
        Assert.Equal("United States", sent["country"]);
        Assert.Equal("Job Board", sent["hearAbout"]);
        Assert.Equal("No", sent["workedBefore"]);
        Assert.Equal("on", sent["app_ApplicantStatementConfirmation"]);
        Assert.Equal("25,1", sent["-1"]);
        Assert.Empty(outcome.Unmapped);
        Assert.Empty(_laterPosts);
    }

    [SkippableFact]
    public async Task An_account_that_already_applied_is_told_so_not_that_no_form_was_found()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sf", SfPacket(), (Pdf, "resume.pdf"), dryRun: true,
            accounts: [new("127.0.0.1", "again@example.com", "Sekrit-9$")]);
        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Contains("already applied", outcome.Error);
        Assert.Contains("again@example.com", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task Without_a_board_account_the_run_names_the_host_that_wants_one_and_where_to_save_it()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sf", Packet(), null, dryRun: true);
        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Contains("no account is saved", outcome.Error);
        Assert.Contains("127.0.0.1", outcome.Error);
        Assert.Contains("Board accounts", outcome.Error);
        Assert.Empty(outcome.Mapped);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_refused_sign_in_is_reported_with_the_host_and_username_and_nothing_is_filled()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/sf", Packet(), null, dryRun: false,
            accounts: [new("127.0.0.1", "ada@example.com", "wrong-password")]);
        Assert.False(outcome.Submitted);
        Assert.False(outcome.Filled);
        Assert.Contains("was refused", outcome.Error);
        Assert.Contains("ada@example.com", outcome.Error);
        Assert.Contains("Invalid email address or password", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task Discovery_signs_in_with_the_board_account_and_reads_the_folded_sections_as_questions()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var discoverer = new FormDiscoverer(new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true, TimeoutSeconds = 60 }, NullLogger<FormDiscoverer>.Instance);
        var questions = await discoverer.DiscoverAsync($"{_fixtureUrl}/jobs/sf", accounts: SfAccount);
        Assert.NotNull(questions);
        Assert.Contains(questions!, q => q.Label.Contains("legally authorized", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(questions!, q => q.Label.Contains("Legal First Name", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(questions!, q => q.Id == "username" || q.Id == "password" || q.Id == "q");
        Assert.Null(await discoverer.DiscoverAsync($"{_fixtureUrl}/jobs/sf"));
    }

    [SkippableFact]
    public async Task An_apply_button_that_never_responds_is_a_clear_reason_not_a_timeout()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/stuck-apply", Packet(), null, dryRun: true);
        Assert.False(outcome.Filled);
        Assert.Contains("no application form was found", outcome.Error);
        Assert.Contains("the Apply button did not respond within 5 s", outcome.Error);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_pre_screening_step_is_answered_and_the_form_behind_it_is_reached()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Paycor opens on a sponsorship radio and a Continue; the run used to dead-end at "no
        // Apply button or link on the page" (#239). It now answers the gate from the packet,
        // presses Continue, and fills the form behind it.
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/prescreen", PreScreenPacket(), (Pdf, "resume.pdf"), dryRun: true);
        Assert.True(outcome.Filled, outcome.Error);
        Assert.False(outcome.Submitted);
        Assert.Empty(outcome.Unmapped);
        // The gate's sponsorship answer plus the six on the form it led to.
        Assert.Contains("sponsorship", outcome.Mapped);
        Assert.Equal(7, outcome.Mapped.Count);
        Assert.EndsWith("/jobs/1", outcome.Url);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task A_pre_screening_step_with_no_answer_to_give_is_named_not_read_as_a_formless_page()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // The sponsorship question is required and the packet has no answer for it: the run
        // must name the step rather than say the packet matched no fields (#239).
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/prescreen", Packet(), (Pdf, "resume.pdf"), dryRun: true);
        Assert.False(outcome.Filled);
        Assert.False(outcome.Submitted);
        Assert.Contains("pre-screening step", outcome.Error);
        Assert.EndsWith("/jobs/prescreen", outcome.Url);
        Assert.Empty(_posts);
    }

    [SkippableFact]
    public async Task Discovery_goes_through_a_pre_screening_step_and_reads_the_form_behind_it()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var discoverer = new FormDiscoverer(new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true, TimeoutSeconds = 60 }, NullLogger<FormDiscoverer>.Instance);
        // With an answer for the gate, discovery goes through to the real form and reads both
        // the gate's question and the form's fields.
        var questions = await discoverer.DiscoverAsync($"{_fixtureUrl}/jobs/prescreen",
            preScreen: q => q.Label.Contains("sponsorship", StringComparison.OrdinalIgnoreCase) ? "No" : null);
        Assert.NotNull(questions);
        Assert.Contains(questions!, q => q.Label.Contains("sponsorship", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(questions!, q => q.Label.Contains("legally authorized", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(questions!, q => q.Label.Contains("Describe a system", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task A_cookie_banner_over_apply_is_dismissed_and_the_form_behind_it_is_filled()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Zoho Recruit and Workable put an "Accept all" over the posting; the Apply click
        // timed out behind it and the run said "no application form was found" (#202).
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/consent", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
    }

    [SkippableFact]
    public async Task A_cookie_banner_that_arrives_after_load_with_a_freeze_layer_is_dismissed_before_apply_is_clicked()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // fyerx on Zoho Recruit: "the Apply button did not respond within 5 s" — every click
        // on "I'm interested" landed on the transparent freeze layer the late consent
        // component put over the page.
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/consent-late", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
    }

    [SkippableTheory]
    [InlineData("consent-unnamed")]
    [InlineData("consent-unnamed-generic")]
    [InlineData("consent-pinned-link")]
    public async Task A_cookie_dialog_named_for_nothing_but_its_text_is_dismissed(string path)
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // UnitedHealth Group on Radancy: "the Apply button did not respond within 5 s", three
        // runs running — the box is "Important System Message" and only its text says cookies (#283).
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/{path}", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
    }

    [SkippableTheory]
    [InlineData("consent-is-the-form")]
    [InlineData("consent-is-an-acknowledgement")]
    public async Task An_agree_button_in_a_dialog_that_asks_for_anything_is_an_answer_not_a_banner(string path)
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/{path}", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        Assert.Equal("Ada", Assert.Single(_posts)["job_application[first_name]"]);
    }

    [SkippableFact]
    public async Task A_cookie_banner_over_the_form_itself_is_dismissed_before_filling()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/consent-form", Packet(), (Pdf, "resume.pdf"), dryRun: false);
        Assert.True(outcome.Submitted, outcome.Error);
        var post = Assert.Single(_posts);
        Assert.Equal("Ada", post["job_application[first_name]"]);
        Assert.Equal("Byte", post["job_application[last_name]"]);
    }

    [SkippableFact]
    public async Task A_posting_answering_410_is_reported_gone_not_formless()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // Five of one day's "no application form was found" runs were Workable 410s (#203).
        Assert.True(BrowserSubmitter.IsClosedPosting("This job is not available anymore"));
        var outcome = await Submitter().RunAsync($"{_fixtureUrl}/jobs/gone-410", Packet(), (Pdf, "resume.pdf"), dryRun: true);
        Assert.True(outcome.Closed);
        Assert.Contains("HTTP 410", outcome.Error);
        Assert.False(outcome.Filled);
        Assert.Empty(_posts);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
