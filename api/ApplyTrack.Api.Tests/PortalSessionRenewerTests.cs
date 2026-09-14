// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
using System.Net;
using ApplyTrack.Api.Agent.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The MyGreenhouse sign-in driven end to end through a real Playwright server at a
/// loopback stand-in for the portal (#221): the email box and Send security code, the
/// eight one-character boxes, the session cookie the portal sets on the right code.
/// Skipped when the Node Playwright is not installed.
/// </summary>
public sealed class PortalSessionRenewerTests : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Cli = Path.Combine(RepoRoot, "node_modules", "playwright-core", "cli.js");
    public static bool Available => File.Exists(Cli);

    private Process? _server;
    private string _ws = "";
    private WebApplication? _fixture;
    private string _fixtureUrl = "";
    private readonly List<string> _codeRequests = [];

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
        // The portal's sign-in as it renders: an email box, Send security code, the identity
        // providers' buttons, a cookie notice with an Ok.
        _fixture.MapGet("/users/sign_in", () => Results.Content(SignInHtml, "text/html"));
        _fixture.MapPost("/users/sign_in", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            lock (_codeRequests) _codeRequests.Add(form["email"].ToString());
            return Results.Content(CodeHtml.Replace("EMAIL", form["email"].ToString()), "text/html");
        });
        _fixture.MapPost("/users/submit_code", async (HttpRequest req, HttpResponse res) =>
        {
            var form = await req.ReadFormAsync();
            if (form["code"] != "szo8ve06")
                return Results.Content(CodeHtml.Replace("EMAIL", form["email"].ToString()).Replace("<!--ERR-->", "<p role=\"alert\" class=\"error\">That code is not valid.</p>"), "text/html");
            res.Cookies.Append("_session_id", "sess-fresh-1", new CookieOptions { HttpOnly = true, Path = "/" });
            return Results.Redirect("/jobs/search");
        });
        _fixture.MapGet("/jobs/search", () => Results.Content("<html><body><h1>Search jobs</h1><input type=\"search\" placeholder=\"Search\" /></body></html>", "text/html"));
        await _fixture.StartAsync();
        _fixtureUrl = _fixture.Urls.First();
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(_fixtureUrl).Host)), "the fixture must be on loopback — never the real portal");
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null) await _fixture.StopAsync();
        if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        _server?.Dispose();
    }

    private PortalSessionRenewer Renewer(int codeWaitSeconds = 30) =>
        new(new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true, TimeoutSeconds = 60 },
            NullLogger<PortalSessionRenewer>.Instance, $"{_fixtureUrl}/users/sign_in", codeWaitSeconds);

    [SkippableFact]
    public async Task The_browser_asks_for_the_code_enters_what_the_mailbox_holds_and_returns_the_session_cookie()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var reads = 0;
        var fresh = await Renewer().RenewAsync("ada@example.com", (since, _) =>
        {
            // The mail is there on the second look, and only mail after the press is read.
            Assert.True(since <= DateTimeOffset.UtcNow);
            return Task.FromResult(++reads >= 2 ? "szo8ve06" : null);
        }, CancellationToken.None);
        Assert.Equal("sess-fresh-1", fresh.Cookie);
        Assert.InRange(fresh.ExpiresAt, DateTimeOffset.UtcNow.AddDays(12), DateTimeOffset.UtcNow.AddDays(14));
        Assert.Equal(["ada@example.com"], _codeRequests);
    }

    [SkippableFact]
    public async Task A_refused_code_is_reported_with_the_portals_words_and_no_session()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(() =>
            Renewer().RenewAsync("ada@example.com", (_, _) => Task.FromResult<string?>("wrong000"), CancellationToken.None));
        Assert.Contains("not valid", ex.Message);
    }

    [SkippableFact]
    public async Task No_mail_in_time_is_a_named_failure_not_a_hang()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(() =>
            Renewer(codeWaitSeconds: 10).RenewAsync("ada@example.com", (_, _) => Task.FromResult<string?>(null), CancellationToken.None));
        Assert.Contains("no security code arrived", ex.Message);
        Assert.Contains("ada@example.com", ex.Message);
    }

    private const string SignInHtml = """
        <html><body>
        <h1>Apply for what's next.</h1>
        <p>Search smarter, apply faster and take control of your job search with MyGreenhouse.</p>
        <form id="login-email-form" method="post" action="/users/sign_in">
          <input id="email-address" name="email" type="email" maxlength="255" aria-label="Email" placeholder="Email" />
          <button type="submit">Send security code</button>
        </form>
        <p>Or continue with</p>
        <form method="post" action="/auth/google"><button type="submit">Continue with Google</button></form>
        <form method="post" action="/auth/apple"><button type="submit">Continue with Apple</button></form>
        <p>Looking for your organization's Greenhouse account? <a href="/orgs">Sign in</a></p>
        <div id="cookies" role="dialog"><p>This site uses cookies</p><button type="button" onclick="document.getElementById('cookies').remove()">Ok</button></div>
        </body></html>
        """;

    // The code page: eight one-character boxes that advance on their own and submit on the last.
    private const string CodeHtml = """
        <html><body>
        <h1>Check your email</h1>
        <p>We sent a security code to EMAIL. Enter it below.</p>
        <!--ERR-->
        <form id="code-form" method="post" action="/users/submit_code">
          <input type="hidden" name="email" value="EMAIL" />
          <input type="hidden" name="code" id="code" />
          <div id="boxes">
            <input maxlength="1" inputmode="text" aria-label="Digit 1" /><input maxlength="1" aria-label="Digit 2" /><input maxlength="1" aria-label="Digit 3" /><input maxlength="1" aria-label="Digit 4" />
            <input maxlength="1" aria-label="Digit 5" /><input maxlength="1" aria-label="Digit 6" /><input maxlength="1" aria-label="Digit 7" /><input maxlength="1" aria-label="Digit 8" />
          </div>
        </form>
        <script>
          const boxes = [...document.querySelectorAll('#boxes input')];
          boxes.forEach((b, i) => b.addEventListener('input', () => {
            if (b.value.length === 1 && i < boxes.length - 1) boxes[i + 1].focus();
            const code = boxes.map(x => x.value).join('');
            if (code.length === boxes.length) { document.getElementById('code').value = code; document.getElementById('code-form').submit(); }
          }));
        </script>
        </body></html>
        """;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "package.json")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
