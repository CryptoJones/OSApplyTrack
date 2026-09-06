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

    [Fact]
    public void Mapping_collapses_duplicates_and_flags_eeo()
    {
        var qs = FormDiscoverer.Map(
        [
            ("", "email", "Email *", "input", "email", true, []),
            ("", "email", "Email *", "input", "email", true, []),
            ("", "veteran", "Veteran status", "select", "select-one", true, ["Yes", "No"]),
            ("", "agree", "I agree to the terms", "input", "checkbox", true, []),
        ]);
        Assert.Equal(3, qs.Count);
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
