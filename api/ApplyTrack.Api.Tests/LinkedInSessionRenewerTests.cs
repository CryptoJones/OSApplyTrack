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
/// The LinkedIn sign-in driven end to end through a real Playwright server at a loopback
/// stand-in for the site (#233): the username and password boxes, Sign in, the
/// <c>li_at</c> + <c>JSESSIONID</c> cookies on the right password, the "check your
/// LinkedIn app" challenge announced once and waited through, the emailed PIN typed in,
/// a refusal named. Skipped when the Node Playwright is not installed.
/// </summary>
public sealed class LinkedInSessionRenewerTests : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Cli = Path.Combine(RepoRoot, "node_modules", "playwright-core", "cli.js");
    public static bool Available => File.Exists(Cli);

    private Process? _server;
    private string _ws = "";
    private WebApplication? _fixture;
    private string _fixtureUrl = "";
    // What the fixture does after a correct password: "feed" signs in at once, "app" shows
    // the app challenge and signs in once /approve is hit, "pin" asks for the mailed PIN.
    private volatile string _mode = "feed";
    private volatile bool _approved;

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
        _fixture.MapGet("/login", () => Results.Content(LoginHtml, "text/html"));
        _fixture.MapPost("/login", async (HttpRequest req, HttpResponse res) =>
        {
            var form = await req.ReadFormAsync();
            if (form["session_key"] != "ada@example.com" || form["session_password"] != "hunter2")
                return Results.Content(LoginHtml.Replace("<!--ERR-->", "<div role=\"alert\" class=\"form__input--error\">Wrong email or password. Try again.</div>"), "text/html");
            // As the site sets it: quoted, verbatim (the cookie API would percent-encode the quotes).
            res.Headers.Append("Set-Cookie", "JSESSIONID=\"ajax:42\"; Path=/");
            switch (_mode)
            {
                case "app": return Results.Redirect("/checkpoint/app");
                case "pin": return Results.Redirect("/checkpoint/pin");
                default: return SignedIn(res);
            }
        });
        _fixture.MapGet("/checkpoint/app", (HttpResponse res) =>
        {
            if (_approved) return SignedIn(res);
            return Results.Content(AppHtml, "text/html");
        });
        _fixture.MapGet("/checkpoint/pin", () => Results.Content(PinHtml, "text/html"));
        _fixture.MapPost("/checkpoint/pin", async (HttpRequest req, HttpResponse res) =>
        {
            var form = await req.ReadFormAsync();
            if (form["pin"] != "482913")
                return Results.Content(PinHtml.Replace("<!--ERR-->", "<p role=\"alert\">That code is not right.</p>"), "text/html");
            return SignedIn(res);
        });
        _fixture.MapGet("/feed/", () => Results.Content("<html><body><h1>Feed</h1></body></html>", "text/html"));
        await _fixture.StartAsync();
        _fixtureUrl = _fixture.Urls.First();
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(_fixtureUrl).Host)), "the fixture must be on loopback — never the real site");
    }

    private static IResult SignedIn(HttpResponse res)
    {
        res.Cookies.Append("li_at", "AQEDA-fresh-1", new CookieOptions { HttpOnly = true, Path = "/", Expires = DateTimeOffset.UtcNow.AddDays(365) });
        return Results.Redirect("/feed/");
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null) await _fixture.StopAsync();
        if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        _server?.Dispose();
    }

    private LinkedInSessionRenewer Renewer(int challengeWaitSeconds = 30) =>
        new(new BrowserOptions { Endpoint = _ws, AllowPrivateTargets = true, TimeoutSeconds = 60 },
            NullLogger<LinkedInSessionRenewer>.Instance, $"{_fixtureUrl}/login", challengeWaitSeconds);

    [SkippableFact]
    public async Task The_browser_signs_in_with_the_password_and_returns_both_cookies_as_json()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        _mode = "feed";
        var moos = new List<string>();
        var fresh = await Renewer().RenewAsync("ada@example.com", "hunter2", null, (m, _) => { moos.Add(m); return Task.CompletedTask; }, CancellationToken.None);
        var cookies = JsonSerializer.Deserialize<Dictionary<string, string>>(fresh.Cookie)!;
        Assert.Equal("AQEDA-fresh-1", cookies["li_at"]);
        Assert.Equal("\"ajax:42\"", cookies["JSESSIONID"]);
        // The cookie outlives the trust window, so the window wins.
        Assert.InRange(fresh.ExpiresAt, DateTimeOffset.UtcNow.AddDays(299), DateTimeOffset.UtcNow.AddDays(301));
        Assert.Empty(moos);
    }

    [SkippableFact]
    public async Task The_app_challenge_is_announced_once_and_waited_through()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        _mode = "app"; _approved = false;
        var moos = new List<string>();
        var renew = Renewer().RenewAsync("ada@example.com", "hunter2", null, (m, token) =>
        {
            moos.Add(m);
            // The person taps Yes a moment after the moo; the fixture then signs in on the next look.
            Task.Run(async () => { await Task.Delay(1500, token); _approved = true; }, token);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var fresh = await renew;
        Assert.Contains("li_at", fresh.Cookie);
        Assert.Equal([LinkedInSessionRenewer.AppTapMessage], moos);
    }

    [SkippableFact]
    public async Task The_mailed_pin_is_read_and_typed_in()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        _mode = "pin";
        var reads = 0;
        Func<DateTimeOffset, CancellationToken, Task<string?>> readCode = (since, _) =>
        {
            Assert.True(since <= DateTimeOffset.UtcNow);
            return Task.FromResult(++reads >= 2 ? "482913" : null);
        };
        var fresh = await Renewer().RenewAsync("ada@example.com", "hunter2", readCode, null, CancellationToken.None);
        Assert.Contains("AQEDA-fresh-1", fresh.Cookie);
    }

    [SkippableFact]
    public async Task A_refused_password_is_reported_with_the_sites_words_and_no_session()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        _mode = "feed";
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(() =>
            Renewer(challengeWaitSeconds: 10).RenewAsync("ada@example.com", "wrong", null, null, CancellationToken.None));
        Assert.Contains("Wrong email or password", ex.Message);
    }

    [SkippableFact]
    public async Task A_pin_with_no_mailbox_is_a_named_failure()
    {
        Skip.IfNot(Available, "Node Playwright is not installed (npm ci)");
        _mode = "pin";
        var ex = await Assert.ThrowsAsync<PortalRenewalException>(() =>
            Renewer(challengeWaitSeconds: 10).RenewAsync("ada@example.com", "hunter2", null, null, CancellationToken.None));
        Assert.Contains("no mailbox", ex.Message);
    }

    // The sign-in as it renders: identity-provider buttons first, then the two boxes and Sign in.
    private const string LoginHtml = """
        <html><body>
        <h1>Sign in</h1>
        <button type="button">Continue with Google</button>
        <button type="button">Sign in with Microsoft</button>
        <button type="button">Sign in with Apple</button>
        <!--ERR-->
        <form method="post" action="/login">
          <label for="u">Email or phone</label><input id="u" name="session_key" type="text" autocomplete="username" />
          <label for="p">Password</label><input id="p" name="session_password" type="password" autocomplete="current-password" />
          <label><input type="checkbox" name="remember" checked /> Keep me signed in</label>
          <button type="submit">Sign in</button>
        </form>
        </body></html>
        """;

    private const string AppHtml = """
        <html><body>
        <h1>Check your LinkedIn app</h1>
        <p>We sent a notification to your signed in devices. Open your LinkedIn app and tap Yes to confirm your sign-in attempt.</p>
        <button type="button">Resend</button>
        <script>setTimeout(() => location.reload(), 1000);</script>
        </body></html>
        """;

    private const string PinHtml = """
        <html><body>
        <h1>Let's do a quick security check</h1>
        <p>Enter the verification code we sent to your email.</p>
        <!--ERR-->
        <form method="post" action="/checkpoint/pin">
          <input name="pin" type="text" inputmode="numeric" autocomplete="one-time-code" aria-label="Verification code" />
          <button type="submit">Submit</button>
        </form>
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
