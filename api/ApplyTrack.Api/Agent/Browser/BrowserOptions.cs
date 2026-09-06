// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>
/// Where the browser lives, bound from the <c>Browser</c> config section
/// (<c>Browser__Endpoint</c> / <c>Browser__Proxy</c>). The browser is never in the API
/// or agent image: the production images are hardened (read-only, no-new-privileges,
/// noexec /tmp) and Chromium cannot run under that, so it is its own container —
/// Playwright's server image running <c>playwright run-server</c> — reached over
/// <c>ws://</c>. Unset means "no browser": the packet still gets prepared and the
/// human uses copy-and-open.
/// </summary>
public sealed class BrowserOptions
{
    /// <summary>The Playwright server, e.g. <c>ws://browser:3000/</c>.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>A forward proxy the browser must use, e.g. <c>http://proxy:3128</c>. With
    /// one set Chromium emits CONNECT and never resolves a name itself, which makes
    /// DNS rebinding structurally impossible at the browser; the browser's own network
    /// has no other route out.</summary>
    public string Proxy { get; set; } = "";

    /// <summary>Overall budget for one fill-and-submit.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Tests only: let the target resolve to loopback/private. Production keeps
    /// the SSRF pre-flight — a job posting is never on a private address.</summary>
    public bool AllowPrivateTargets { get; set; }

    public bool IsConfigured => Endpoint.Trim().Length > 0;
}
