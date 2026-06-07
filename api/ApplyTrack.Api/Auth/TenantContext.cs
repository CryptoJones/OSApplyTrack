// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Auth;

/// <summary>
/// Per-request tenant identity. <see cref="UserId"/> is set once by
/// <see cref="TenantMiddleware"/> from the session cookie — the single point where a
/// tenant_id enters the system. Endpoints never read this directly; DI builds their
/// repos already scoped to <see cref="TenantId"/>. For v1 <c>tenant_id == user.id</c>.
/// </summary>
public sealed class TenantContext
{
    public long? UserId { get; set; }

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
