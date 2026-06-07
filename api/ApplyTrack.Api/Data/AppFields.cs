// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Data;

/// <summary>
/// The structured contents of a single job-application note — the C# heir to the
/// Python <c>AppFields</c>. Serializes to the snake_case JSON shape the SPA reads
/// (the global naming policy maps <c>ContactEmail</c> &lt;-&gt; <c>contact_email</c>).
/// </summary>
public sealed record AppFields
{
    public string Company { get; init; } = "";
    public string Role { get; init; } = "";
    public string Lane { get; init; } = "ai";
    public string Status { get; init; } = "lead";
    public string Link { get; init; } = "";
    public string Location { get; init; } = "";
    public string Salary { get; init; } = "";
    public string Source { get; init; } = "";
    public string Contact { get; init; } = "";
    public string ContactEmail { get; init; } = "";
    public string Applied { get; init; } = "";
    public string Followup { get; init; } = "";
    public string Created { get; init; } = "";
    public string Score { get; init; } = "";
    public string Notes { get; init; } = "";

    // Pipeline stages in order; the UI colour-codes and the sidebar sorts by this.
    public static readonly string[] Statuses =
        ["lead", "ready", "applied", "screen", "onsite", "offer", "rejected", "passed"];

    // Which strength the role leads with — mirrors the cover-letter lane switch.
    public static readonly string[] Lanes = ["dotnet", "devrel", "ai"];

    /// <summary>Trim every field and clamp lane/status to known values (heir to from_dict).</summary>
    public AppFields Normalized()
    {
        static string S(string? v) => (v ?? "").Trim();
        var lane = S(Lane).ToLowerInvariant();
        var status = S(Status).ToLowerInvariant();
        return this with
        {
            Company = S(Company),
            Role = S(Role),
            Lane = Lanes.Contains(lane) ? lane : "ai",
            Status = Statuses.Contains(status) ? status : "lead",
            Link = S(Link),
            Location = S(Location),
            Salary = S(Salary),
            Source = S(Source),
            Contact = S(Contact),
            ContactEmail = S(ContactEmail),
            Applied = S(Applied),
            Followup = S(Followup),
            Created = S(Created),
            Score = S(Score),
            Notes = (Notes ?? "").TrimEnd(),
        };
    }
}
