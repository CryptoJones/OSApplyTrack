// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
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
/// Read-only discovery of a Lever-shaped form through a real Playwright server at a
/// loopback fixture: every control comes back with its accessible name, field name,
/// type, options and required flag; EEO is flagged; nothing is submitted. The pure
/// mapping is also covered without a browser.
/// </summary>
public sealed class FormDiscovererTests : IAsyncLifetime
{
    private Process? _server;
    private string _ws = "";
    private WebApplication? _fixture;
    private string _url = "";
    private int _posts;

    public async Task InitializeAsync()
    {
        if (!BrowserSubmitterTests.Available) return;
        var cli = Path.Combine(FindRepoRoot(), "node_modules", "playwright-core", "cli.js");
        _server = Process.Start(new ProcessStartInfo("node", $"\"{cli}\" run-server --port 0 --host 127.0.0.1")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        })!;
        while (await _server.StandardOutput.ReadLineAsync() is { } line)
        {
            var i = line.IndexOf("ws://", StringComparison.Ordinal);
            if (i >= 0) { _ws = line[i..].Trim(); break; }
        }
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _fixture = builder.Build();
        _fixture.MapGet("/acme/1234/apply", () => Results.Content(LeverHtml, "text/html"));
        // The same form, loaded into an iframe by an Apply click; and fetched late into the page.
        _fixture.MapGet("/framed", () => Results.Content(
            "<html><body><h1>Job</h1><button type=\"button\" onclick=\"const f=document.createElement('iframe');f.src='/acme/1234/apply';f.style.width='700px';f.style.height='900px';document.body.appendChild(f);this.remove()\">Apply for this job</button></body></html>", "text/html"));
        _fixture.MapGet("/late", () => Results.Content(
            "<html><body><h1>Job</h1><div id=\"slot\">Fetching application form</div><script>setTimeout(()=>{fetch('/acme/1234/apply').then(r=>r.text()).then(h=>{document.getElementById('slot').innerHTML=h.replace(/^[\\s\\S]*<body>/,'').replace(/<\\/body>[\\s\\S]*$/,'');});},1500)</script></body></html>", "text/html"));
        // Zoho Recruit's shape (#207): web-component fields with no label association.
        _fixture.MapGet("/zoho", () => Results.Content(ZohoHtml, "text/html"));
        _fixture.MapPost("/acme/1234/apply", () => { Interlocked.Increment(ref _posts); return Results.Content("nope"); });
        await _fixture.StartAsync();
        _url = _fixture.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null) await _fixture.StopAsync();
        if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        _server?.Dispose();
    }

    private const string LeverHtml = """
        <html><body><form method="post">
          <label>Full name *<input name="name" required /></label>
          <label>Email *<input name="email" type="email" required /></label>
          <label>Phone<input name="phone" type="tel" /></label>
          <label>Resume/CV *<input name="resume" type="file" /></label>
          <label>LinkedIn URL<input name="urls[LinkedIn]" /></label>
          <label for="q1">Why Acme? *</label><textarea id="q1" name="cards[abc][field0]" required></textarea>
          <label for="q2">Are you authorized to work in the US?</label>
          <select id="q2" name="cards[abc][field1]"><option value="">Select</option><option>Yes</option><option>No</option></select>
          <fieldset><legend>Remote preference</legend>
            <input type="radio" id="r1" name="cards[abc][field2]" value="Remote" /><label for="r1">Remote</label>
            <input type="radio" id="r2" name="cards[abc][field2]" value="Office" /><label for="r2">Office</label>
          </fieldset>
          <label for="eeo">Gender</label><select id="eeo" name="eeo[gender]"><option>Female</option><option>Male</option><option>Decline</option></select>
          <input type="hidden" name="token" value="x" />
          <button type="submit">Submit application</button>
        </form></body></html>
        """;

    // Zoho Recruit's career-site form as fyerx runs it, reduced: every field is a
    // <crux-*-component cx-prop-label="…"> around an input with no id and no <label for>;
    // the row above carries a plain <label>; the First Name row also holds a Salutation
    // picklist (a div combobox, not an input); City/State/Zip share id="inputId".
    private const string ZohoHtml = """
        <html><body><form>
          <div class="crc-form-row"><label class="crm-from-label"><span>First Name</span></label>
            <div class="crc-form-field">
              <crux-picklist-component cx-prop-label="Salutation"><div role="combobox"><span>-None-</span></div></crux-picklist-component>
              <crux-text-component cx-prop-label="First Name"><div><input type="text" name="rec-form_50429000000003149" /></div></crux-text-component>
            </div></div>
          <div class="crc-form-row"><label class="crm-from-label"><span>Last Name</span><span class="required">*</span></label>
            <div class="crc-form-field"><crux-text-component cx-prop-label="Last Name"><div><input type="text" name="rec-form_50429000000003151" required /></div></crux-text-component></div></div>
          <div class="crc-form-row"><label class="crm-from-label"><span>Email</span></label>
            <div class="crc-form-field"><crux-email-component cx-prop-label="Email"><div><input type="text" name="rec-form_50429000000003155" /></div></crux-email-component></div></div>
          <div class="crc-form-row"><label class="crm-from-label"><span>City</span></label>
            <div class="crc-form-field"><crux-text-component cx-prop-label="City"><div><input type="text" id="inputId" /></div></crux-text-component></div></div>
          <div class="crc-form-row"><label class="crm-from-label"><span>State/Province</span></label>
            <div class="crc-form-field"><crux-text-component cx-prop-label="State/Province"><div><input type="text" id="inputId" /></div></crux-text-component></div></div>
          <div class="crc-form-row"><label class="crm-from-label"><span>Country</span></label>
            <div class="crc-form-field"><crux-text-component cx-prop-label="Country"><div><input type="text" name="rec-form_50429000000003177" /></div></crux-text-component></div></div>
          <!-- A row whose component names nothing: the row's own label must serve. -->
          <div class="crc-form-row"><label class="crm-from-label"><span>LinkedIn</span></label>
            <div class="crc-form-field"><crux-website-component><div><input type="text" name="rec-form_50429000005978001" /></div></crux-website-component></div></div>
          <div class="crc-form-row"><label class="crm-from-label"><span>Resume</span></label>
            <div class="crc-form-field"><rec-file-upload-component><input type="file" name="rec-form_50429000000017197_file" /></rec-file-upload-component></div></div>
          <button type="button">Submit Application</button>
        </form></body></html>
        """;

    [SkippableFact]
    public async Task A_zoho_form_with_no_label_association_still_gets_its_labels_and_keeps_every_field()
    {
        Skip.IfNot(BrowserSubmitterTests.Available, "Node Playwright is not installed (npm ci)");
        var discoverer = new FormDiscoverer(
            new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true }, NullLogger<FormDiscoverer>.Instance);

        var questions = await discoverer.DiscoverAsync($"{_url}/zoho");

        Assert.NotNull(questions);
        var byId = questions!.ToDictionary(q => q.Id);
        Assert.Equal("First Name", byId["rec-form_50429000000003149"].Label);
        Assert.Equal("Last Name", byId["rec-form_50429000000003151"].Label);
        Assert.True(byId["rec-form_50429000000003151"].Required);
        Assert.Equal("Email", byId["rec-form_50429000000003155"].Label);
        Assert.Equal(PacketQuestion.Standard, byId["rec-form_50429000000003155"].Kind);
        Assert.Equal("Country", byId["rec-form_50429000000003177"].Label);
        Assert.Equal("LinkedIn", byId["rec-form_50429000005978001"].Label);   // from the row's <label>
        Assert.Equal(PacketQuestion.File, byId["rec-form_50429000000017197_file"].Type);
        Assert.Equal("Resume", byId["rec-form_50429000000017197_file"].Label);
        // The shared id keeps the first box; the second is keyed by its own label.
        Assert.Equal("City", byId["inputId"].Label);
        Assert.Equal("State/Province", byId["State/Province"].Label);
        Assert.Equal(0, _posts);
    }

    [SkippableFact]
    public async Task Discovers_a_lever_shaped_form_without_touching_it()
    {
        Skip.IfNot(BrowserSubmitterTests.Available, "Node Playwright is not installed (npm ci)");
        var discoverer = new FormDiscoverer(
            new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true }, NullLogger<FormDiscoverer>.Instance);

        var questions = await discoverer.DiscoverAsync($"{_url}/acme/1234/apply");

        Assert.NotNull(questions);
        var byId = questions!.ToDictionary(q => q.Id);
        Assert.Equal(PacketQuestion.Standard, byId["name"].Kind);
        Assert.True(byId["name"].Required);
        Assert.Equal("Full name", byId["name"].Label);
        Assert.Equal(PacketQuestion.File, byId["resume"].Type);
        Assert.Equal(PacketQuestion.Standard, byId["urls[LinkedIn]"].Kind);
        Assert.Equal(PacketQuestion.Textarea, byId["cards[abc][field0]"].Type);
        Assert.True(byId["cards[abc][field0]"].Required);
        Assert.Equal(PacketQuestion.Custom, byId["cards[abc][field0]"].Kind);
        Assert.Equal(["Yes", "No"], byId["cards[abc][field1]"].Options);   // the blank "Select" is dropped... 
        Assert.Equal(PacketQuestion.Select, byId["cards[abc][field2]"].Type);
        Assert.Equal(["Remote", "Office"], byId["cards[abc][field2]"].Options);
        Assert.Equal(PacketQuestion.Eeo, byId["eeo[gender]"].Kind);
        Assert.False(byId.ContainsKey("token"));
        Assert.Equal(0, _posts);
    }

    [SkippableFact]
    public async Task Discovers_a_form_that_arrives_in_an_iframe_or_after_the_page_is_idle()
    {
        Skip.IfNot(BrowserSubmitterTests.Available, "Node Playwright is not installed (npm ci)");
        var discoverer = new FormDiscoverer(
            new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true }, NullLogger<FormDiscoverer>.Instance);

        foreach (var path in new[] { "/framed", "/late" })
        {
            var questions = await discoverer.DiscoverAsync($"{_url}{path}");
            Assert.NotNull(questions);
            var byId = questions!.ToDictionary(q => q.Id);
            Assert.Equal("Full name", byId["name"].Label);
            Assert.Equal(PacketQuestion.Custom, byId["cards[abc][field0]"].Kind);
            Assert.Equal(["Remote", "Office"], byId["cards[abc][field2]"].Options);
        }
        Assert.Equal(0, _posts);
    }

    [Fact]
    public void Mapping_collapses_duplicates_and_flags_eeo()
    {
        var qs = FormDiscoverer.Map(
        [
            ("", "email", "Email *", "input", "email", true, []),
            ("", "email", "Email *", "input", "email", true, []),
            ("", "veteran", "Veteran status", "select", "select-one", true, ["Yes", "No"]),
            ("", "agree", "I agree to the terms", "input", "checkbox", true, []),
            ("", "", "What are the database technologies you are proficient with? *", "input", "text", true, []),
            ("", "", "", "input", "text", false, []),
            // Zoho's City / State / Zip: one id for three boxes (#207).
            ("inputId", "", "City", "input", "text", false, []),
            ("inputId", "", "State/Province", "input", "text", false, []),
            ("inputId", "", "State/Province", "input", "text", false, []),
        ]);
        Assert.Equal(6, qs.Count);
        Assert.Equal(("inputId", "City"), (qs[4].Id, qs[4].Label));
        Assert.Equal(("State/Province", "State/Province"), (qs[5].Id, qs[5].Label));
        // No name, no id: keyed by the label (Comeet's custom questions); nothing at all is dropped.
        Assert.Equal("What are the database technologies you are proficient with?", qs[3].Id);
        Assert.True(qs[3].Required);
        Assert.Equal("Email", qs[0].Label);
        Assert.Equal(PacketQuestion.Eeo, qs[1].Kind);
        Assert.False(qs[1].Required);   // EEO is never required of the agent
        Assert.Equal(["Yes", "No"], qs[2].Options);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
