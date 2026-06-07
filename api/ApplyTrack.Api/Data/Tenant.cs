// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Data;

/// <summary>
/// Step 1 is single-user: every query is scoped to one hardcoded tenant. Real
/// per-session tenancy (and the choke-point that resolves it) lands in Step 2.
/// </summary>
public static class Tenant
{
    public const long BootstrapId = 1;
}
