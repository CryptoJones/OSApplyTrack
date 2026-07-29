// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Human-facing API limits. These bound persisted rows and LLM prompt material at
/// the model edge; request-byte limits are enforced separately by middleware.
/// </summary>
public static class InputLimits
{
    public const int Company = 256;
    public const int Role = 256;
    public const int Lane = 32;
    public const int Status = 32;
    public const int Link = 2048;
    public const int ApplicationField = 512;
    public const int ContactEmail = 320;
    public const int ApplicationMetadata = 128;
    public const int Notes = 64 * 1024;
    public const int RawApplication = Notes + 8 * 1024;

    public const int Keywords = 100;
    public const int Keyword = 100;
    public const int ExcludedLocations = 100;
    public const int ExcludedLocation = 200;
    public const int AtsBoards = 50;
    public const int AtsSlug = 200;

    public const int ResumeExperience = 50;
    public const int ResumeHighlights = 50;
    public const int ResumeSkills = 200;
    public const int ResumeCertifications = 100;
    public const int ResumeLinks = 50;
    public const int ResumeHeading = 300;
    public const int ResumeSummary = 256 * 1024;
    public const int ResumeItem = 2000;

    public const int LlmBaseUrl = 2048;
    public const int LlmModel = 200;
    public const int LlmApiKey = 8192;
    public const int CoverLetterSignature = 16 * 1024;

    public static void ValidateApplication(AppFields fields)
    {
        Text("company", fields.Company, Company);
        Text("role", fields.Role, Role);
        Text("lane", fields.Lane, Lane);
        Text("status", fields.Status, Status);
        Text("link", fields.Link, Link);
        Text("location", fields.Location, ApplicationField);
        Text("salary", fields.Salary, ApplicationField);
        Text("source", fields.Source, ApplicationField);
        Text("contact", fields.Contact, ApplicationField);
        Text("contact_email", fields.ContactEmail, ContactEmail);
        Text("applied", fields.Applied, ApplicationMetadata);
        Text("followup", fields.Followup, ApplicationMetadata);
        Text("created", fields.Created, ApplicationMetadata);
        Text("score", fields.Score, ApplicationMetadata);
        Text("notes", fields.Notes, Notes);
    }

    public static void ValidateCriteria(JsonElement data)
    {
        Array(data, "keywords", Keywords, Keyword);
        Array(data, "exclude_locations", ExcludedLocations, ExcludedLocation);

        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("ats_boards", out var boards)
            || boards.ValueKind != JsonValueKind.Array)
            return;

        Count("ats_boards", boards.GetArrayLength(), AtsBoards);
        foreach (var board in boards.EnumerateArray())
        {
            if (board.ValueKind != JsonValueKind.Object) continue;
            JsonText(board, "provider", Keyword);
            JsonText(board, "slug", AtsSlug);
        }
    }

    public static void ValidateResume(JsonElement data)
    {
        JsonText(data, "full_name", ResumeHeading);
        JsonText(data, "headline", ResumeHeading);
        JsonText(data, "location", ResumeHeading);
        JsonText(data, "summary", ResumeSummary);
        Array(data, "skills", ResumeSkills, ResumeItem);
        Array(data, "certifications", ResumeCertifications, ResumeItem);

        if (data.ValueKind != JsonValueKind.Object)
            return;

        if (data.TryGetProperty("experience", out var experience)
            && experience.ValueKind == JsonValueKind.Array)
        {
            Count("experience", experience.GetArrayLength(), ResumeExperience);
            foreach (var entry in experience.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                JsonText(entry, "company", ResumeHeading);
                JsonText(entry, "title", ResumeHeading);
                JsonText(entry, "dates", ResumeHeading);
                Array(entry, "highlights", ResumeHighlights, ResumeItem);
            }
        }

        if (data.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            Count("links", links.GetArrayLength(), ResumeLinks);
            foreach (var entry in links.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                JsonText(entry, "label", ResumeHeading);
                JsonText(entry, "url", Link);
            }
        }
    }

    public static string Text(string field, string? value, int maximum)
    {
        var text = value ?? "";
        if (text.Length > maximum)
            throw new AppValidationException(
                $"field '{field}' exceeds the maximum length of {maximum} characters");
        return text;
    }

    public static void Count(string field, int count, int maximum)
    {
        if (count > maximum)
            throw new AppValidationException(
                $"field '{field}' accepts at most {maximum} items");
    }

    private static void Array(JsonElement data, string field, int maximum, int itemMaximum)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(field, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return;

        Count(field, array.GetArrayLength(), maximum);
        foreach (var item in array.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : item.ToString();
            Text($"{field} item", value, itemMaximum);
        }
    }

    private static void JsonText(JsonElement data, string field, int maximum)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty(field, out var value))
            return;
        Text(field, value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString(), maximum);
    }
}
