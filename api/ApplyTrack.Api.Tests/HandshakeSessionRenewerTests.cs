// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ApplyTrack.Api.Agent.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The Handshake sign-in driven end to end through a real Playwright server at a loopback
/// stand-in for the site (#267): the school email first, the school's own LDAP form behind
/// it, the <c>hss-global</c> cookie on the right password, a refusal named, and a bare
/// username refused before the browser is ever opened. Skipped when the Node Playwright is
/// not installed.
/// </summary>
public sealed class HandshakeSessionRenewerTests : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Cli = Path.Combine(RepoRoot, "node_modules", "playwright-core", "cli.js");
    public static bool Available => File.Exists(Cli);

    private Process? _server;
    private string _ws = "";
    private WebApplication? _fixture;
    private string _fixtureUrl = "";
    private volatile bool _anonymousCookie;
    private volatile bool _federated;

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

        // Step one: the school email, which is how Handshake finds the school.
        _fixture.MapGet("/access", (HttpResponse res) =>
        {
            // A school whose site hands out an anonymous session cookie on first load —
            // the shape that would let a naive renewer seal an unauthenticated session.
            if (_anonymousCookie)
                res.Cookies.Append("hss-global", "hs-anonymous", new CookieOptions { HttpOnly = true, Path = "/" });
            return Results.Content(EmailHtml, "text/html");
        });
        _fixture.MapPost("/access", async (HttpRequest req, HttpResponse res) =>
        {
            var form = await req.ReadFormAsync();
            if (form["email"] != "ada@eastern.edu")
                return Results.Content(EmailHtml.Replace("<!--ERR-->", "<div role=\"alert\">That email is not recognized.</div>"), "text/html");
            if (_federated) return Results.Redirect("/access/sso/microsoft");
            return Results.Redirect("/access/sso/ldap");
        });
        _fixture.MapGet("/access/sso/microsoft", () =>
            Results.Content("<html><body><h1>Sign in</h1><p>Enter code from your authenticator app</p></body></html>", "text/html"));

        // Step two: the school's own form, which takes the same address.
        _fixture.MapGet("/access/sso/ldap", () => Results.Content(SchoolHtml, "text/html"));
        _fixture.MapPost("/access/sso/ldap", async (HttpRequest req, HttpResponse res) =>
        {
            var form = await req.ReadFormAsync();
            if (form["username"] != "ada@eastern.edu" || form["password"] != "hunter2")
                return Results.Content(SchoolHtml.Replace("<!--ERR-->", "<div role=\"alert\">Wrong username or password. Try again.</div>"), "text/html");
            // With _anonymousCookie the site keeps the value it handed out before the
            // password — so "a cookie exists" must not be mistaken for "signed in".
            res.Cookies.Append("hss-global", _anonymousCookie ? "hs-anonymous" : "hs-fresh-1", new CookieOptions
            {
                HttpOnly = true, Path = "/", Expires = DateTimeOffset.UtcNow.AddDays(90),
            });
            return Results.Redirect("/home");
        });
        _fixture.MapGet("/home", () => Results.Content("<html><body><h1>Welcome back, Ada</h1></body></html>", "text/html"));
        await _fixture.StartAsync();
        _fixtureUrl = _fixture.Urls.First();
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(_fixtureUrl).Host)), "the fixture must be on loopback — never the real site");
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null) await _fixture.StopAsync();
        if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        _server?.Dispose();
    }

    private HandshakeSessionRenewer Renewer(int stepWaitSeconds = 30) =>
        new(new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true, TimeoutSeconds = 60 },
            NullLogger<HandshakeSessionRenewer>.Instance, $"{_fixtureUrl}/access", stepWaitSeconds);

    [SkippableFact]
    public async Task The_browser_signs_in_with_the_school_email_and_returns_the_cookie_as_json()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var fresh = await Renewer().RenewAsync("ada@eastern.edu", "hunter2", CancellationToken.None);
        var cookies = JsonSerializer.Deserialize<Dictionary<string, string>>(fresh.Cookie)!;
        Assert.Equal("hs-fresh-1", cookies["hss-global"]);
        // The cookie's own end, less a day of slack, and never past the ninety-day life.
        Assert.True(fresh.ExpiresAt > DateTimeOffset.UtcNow.AddDays(80), $"expires {fresh.ExpiresAt:u}");
        Assert.True(fresh.ExpiresAt <= DateTimeOffset.UtcNow.AddDays(HandshakeSessionRenewer.SessionLife.TotalDays));
    }

    [SkippableFact]
    public async Task A_wrong_password_is_refused_and_the_page_is_quoted()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(
            () => Renewer().RenewAsync("ada@eastern.edu", "wrong", CancellationToken.None));
        Assert.Contains("Handshake refused the sign-in", ex.Message);
    }

    [SkippableFact]
    public async Task An_email_handshake_does_not_know_is_refused_by_name()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(
            () => Renewer().RenewAsync("nobody@example.com", "hunter2", CancellationToken.None));
        // The message names the host it landed on, so the failure is legible rather than a
        // timeout that blames Handshake.
        Assert.Contains("does not drive", ex.Message);
    }

    [SkippableFact]
    public async Task A_school_that_federates_or_wants_a_second_factor_is_named()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        _federated = true;
        try
        {
            var ex = await Assert.ThrowsAsync<PortalRenewalException>(
                () => Renewer(stepWaitSeconds: 15).RenewAsync("ada@eastern.edu", "hunter2", CancellationToken.None));
            Assert.Contains("identity provider or a second factor", ex.Message);
        }
        finally { _federated = false; }
    }

    [SkippableFact]
    public async Task An_anonymous_cookie_handed_out_on_first_load_is_not_sealed()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        // A site that sets its session cookie on page load, unauthenticated: "a cookie
        // named hss-global exists" is not the same as "signed in".
        _anonymousCookie = true;
        try
        {
            var ex = await Assert.ThrowsAsync<PortalRenewalException>(
                () => Renewer(stepWaitSeconds: 15).RenewAsync("ada@eastern.edu", "hunter2", CancellationToken.None));
            Assert.Contains("did not finish", ex.Message);
        }
        finally { _anonymousCookie = false; }
    }

    [Fact]
    public async Task A_bare_username_is_refused_without_opening_a_browser()
    {
        // Handshake routes to the school by the address, so a bare username cannot work —
        // say so plainly rather than driving a browser into a dead end.
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(
            () => Renewer().RenewAsync("ada", "hunter2", CancellationToken.None));
        Assert.Contains("school email address", ex.Message);
    }

    [Fact]
    public async Task An_account_with_no_password_is_refused_without_opening_a_browser()
    {
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(
            () => Renewer().RenewAsync("ada@eastern.edu", "", CancellationToken.None));
        Assert.Contains("no password saved", ex.Message);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir.Length > 0 && !File.Exists(Path.Combine(dir, "package.json")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    private const string EmailHtml = """
        <html><body>
        <h1>Log in or sign up</h1>
        <form method="post" action="/access">
          <input type="text" name="email" placeholder="Email" />
          <button type="submit">Continue with email</button>
        </form>
        <!--ERR-->
        </body></html>
        """;

    private const string SchoolHtml = """
        <html><body>
        <h1>Welcome to Handshake</h1>
        <form method="post" action="/access/sso/ldap">
          <input type="text" name="username" placeholder="School username or email" />
          <input type="password" name="password" />
          <button type="submit">Continue</button>
        </form>
        <!--ERR-->
        </body></html>
        """;
}
