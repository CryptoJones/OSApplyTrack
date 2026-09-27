// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Auth;

/// <summary>
/// Per-request tenant identity. <see cref="UserId"/> is set once by
/// <see cref="TenantMiddleware"/> from the session cookie or a personal API token — the single point where a
/// tenant_id enters the system. Endpoints never read this directly; DI builds their
/// repos already scoped to <see cref="TenantId"/>. For v1 <c>tenant_id == user.id</c>.
/// </summary>
public sealed class TenantContext
{
    public long? UserId { get; set; }

    /// <summary>The stored (hashed) id of the session this request rode in on, so account
    /// endpoints can tell "this browser" apart from the user's other sessions.</summary>
    public string? SessionId { get; set; }

    /// <summary>The personal API token (#356) this request rode in on instead of a session, and
    /// its scope — null for a browser session. Drives the per-token rate limit and what the
    /// tenancy middleware lets a token reach.</summary>
    public long? TokenId { get; set; }

    public string? TokenScope { get; set; }

    public bool IsAuthenticated => UserId is not null;

    /// <summary>
    /// The tenant_id scoped repos are built with. Throws if read before a session was
    /// resolved — a guard so a repo can never be constructed outside an authenticated
    /// request (protected routes 401 in the middleware before reaching this).
    /// </summary>
    public long TenantId => UserId
        ?? throw new InvalidOperationException(
            "No tenant resolved for this request (scoped repo built outside an authenticated scope).");
}
