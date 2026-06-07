// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Data;

/// <summary>Lightweight listing entry for the SPA sidebar (heir to AppSummary).</summary>
public sealed record AppSummary
{
    public string Filename { get; init; } = "";
    public string Company { get; init; } = "";
    public string Role { get; init; } = "";
    public string Lane { get; init; } = "";
    public string Status { get; init; } = "";
    public string Contact { get; init; } = "";
    public string ContactEmail { get; init; } = "";
    public string Applied { get; init; } = "";
    public string Followup { get; init; } = "";
    public string Score { get; init; } = "";
    public string Link { get; init; } = "";
    public string Snippet { get; init; } = "";
}
