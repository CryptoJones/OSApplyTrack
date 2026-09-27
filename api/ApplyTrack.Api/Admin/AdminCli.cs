// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Globalization;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Admin;

/// <summary>
/// The operator's command line (#358): <c>dotnet ApplyTrack.Api.dll admin &lt;command&gt;</c>.
/// There is no admin HTTP API on purpose — a web route that can disable accounts or hand
/// out auto-apply is one more thing to steal a session for. The CLI runs where the
/// operator already has the owner's connection string (the api container's environment),
/// and nowhere else: the agent's and the poller's roles cannot read what it reads.
///
/// Like the workers' fan-out, these are the privileged cross-tenant statements; each
/// per-tenant figure is still filtered <c>WHERE tenant_id = u.id</c>.
/// </summary>
public static class AdminCli
{
    public const string Usage = """
        usage: dotnet ApplyTrack.Api.dll admin <command>

          users                      every account: id, email, status, agent allowlist, created
          usage [<user>]             per account: applications, cover letters, agent evidence
                                     (rows and bytes), live sessions, last sign-in, last seen,
                                     last poll
          allow <user> [note...]     put the account on the auto-apply allowlist
          disallow <user>            take it off the allowlist
          disable <user>             stop the account: its sessions and API tokens stop working
                                     on their next request, no sign-in link is sent, and the
                                     poller and agent skip it. Its data is kept.
          enable <user>              let a disabled account back in

        <user> is an account id or its email address. Connects with ConnectionStrings__Postgres
        (the owner's connection — run it in the api container, not the agent or poller).
        """;

    /// <summary>Is this process invocation the admin CLI rather than the web host?</summary>
    public static bool IsAdmin(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>The connection string the web host would use, read the same way (appsettings,
    /// then the environment) without building the host — no migration, no key file, no port.</summary>
    public static string? ConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{env}.json", optional: true)
            .AddEnvironmentVariables()
            .Build()
            .GetConnectionString("Postgres");
    }

    /// <summary>Runs one command (the arguments after <c>admin</c>); returns the exit code:
    /// 0 done, 1 not found / failed, 2 bad usage.</summary>
    public static async Task<int> RunAsync(string[] args, string? connectionString, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            (args.Length == 0 ? error : output).WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            error.WriteLine("admin: no Postgres connection string (set ConnectionStrings__Postgres).");
            return 1;
        }
        var cmd = args[0].ToLowerInvariant();
        var rest = args[1..];
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            // Owner only, checked before any command runs: the agent's role can SELECT every
            // table, so a read like `users` would otherwise work from the agent container.
            // Whoever owns `users` (or a superuser) is the migrating owner; nobody else is.
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT pg_has_role(current_user, relowner, 'MEMBER') FROM pg_class WHERE oid = 'public.users'::regclass"))
            {
                error.WriteLine($"admin: {await conn.ExecuteScalarAsync<string>("SELECT current_user")} is not the database "
                    + "owner — run this with the owner's connection (the api container), not the agent's or the poller's role.");
                return 1;
            }
            return cmd switch
            {
                "users" when rest.Length == 0 => await UsersAsync(conn, output),
                "usage" when rest.Length <= 1 => await UsageAsync(conn, rest.FirstOrDefault(), output, error),
                "allow" when rest.Length >= 1 => await AllowAsync(conn, rest[0], string.Join(' ', rest[1..]), output, error),
                "disallow" when rest.Length == 1 => await DisallowAsync(conn, rest[0], output, error),
                "disable" when rest.Length == 1 => await SetStatusAsync(conn, rest[0], "disabled", output, error),
                "enable" when rest.Length == 1 => await SetStatusAsync(conn, rest[0], "active", output, error),
                _ => BadUsage(error),
            };
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            error.WriteLine($"admin: {ex.MessageText} — run this with the owner's connection "
                + "(the api container), not the agent's or the poller's role.");
            return 1;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            error.WriteLine($"admin: {ex.MessageText} — start the api once so it migrates the database, then retry.");
            return 1;
        }
        catch (NpgsqlException ex)
        {
            error.WriteLine($"admin: {ex.Message}");
            return 1;
        }
    }

    private static int BadUsage(TextWriter error)
    {
        error.WriteLine(Usage);
        return 2;
    }

    private sealed record UserRow(long Id, string Email, string Status, bool Allowed, DateTime CreatedAt);

    private static async Task<int> UsersAsync(NpgsqlConnection conn, TextWriter output)
    {
        var rows = await conn.QueryAsync<UserRow>(
            """
            SELECT u.id, u.email::text AS email, u.status, a.tenant_id IS NOT NULL AS allowed, u.created_at AS createdat
            FROM users u LEFT JOIN agent_allowlist a ON a.tenant_id = u.id
            ORDER BY u.id
            """);
        Table(output, ["id", "email", "status", "agent", "created"],
            rows.Select(r => new[] { Num(r.Id), r.Email, r.Status, r.Allowed ? "allowed" : "-", When(r.CreatedAt) }));
        return 0;
    }

    private sealed record UsageRow(
        long Id, string Email, string Status, long Applications, long CoverLetters, long EvidenceRows,
        long EvidenceBytes, long Sessions, DateTime? LastLogin, DateTime? LastSeen, DateTime? LastPolled);

    private static async Task<int> UsageAsync(NpgsqlConnection conn, string? who, TextWriter output, TextWriter error)
    {
        long? only = null;
        if (who is not null)
        {
            only = await ResolveAsync(conn, who);
            if (only is null) return NotFound(error, who);
        }
        var rows = await conn.QueryAsync<UsageRow>(
            """
            SELECT u.id, u.email::text AS email, u.status,
                   (SELECT count(*) FROM applications WHERE tenant_id = u.id) AS applications,
                   (SELECT count(*) FROM cover_letters WHERE tenant_id = u.id) AS coverletters,
                   e.n AS evidencerows, e.bytes AS evidencebytes,
                   (SELECT count(*) FROM sessions WHERE user_id = u.id AND expires_at > now()) AS sessions,
                   (SELECT max(created_at) FROM sessions WHERE user_id = u.id) AS lastlogin,
                   (SELECT max(last_seen_at) FROM sessions WHERE user_id = u.id) AS lastseen,
                   u.last_polled_at AS lastpolled
            FROM users u
            CROSS JOIN LATERAL (
                SELECT count(*) AS n, coalesce(sum(octet_length(screenshot)), 0)::bigint AS bytes
                FROM agent_evidence WHERE tenant_id = u.id) e
            WHERE @only::bigint IS NULL OR u.id = @only
            ORDER BY u.id
            """,
            new { only });
        Table(output,
            ["id", "email", "status", "apps", "letters", "evidence", "evidence_bytes", "sessions", "last_login", "last_seen", "last_poll"],
            rows.Select(r => new[]
            {
                Num(r.Id), r.Email, r.Status, Num(r.Applications), Num(r.CoverLetters), Num(r.EvidenceRows),
                Num(r.EvidenceBytes), Num(r.Sessions), When(r.LastLogin), When(r.LastSeen), When(r.LastPolled),
            }));
        return 0;
    }

    private static async Task<int> AllowAsync(NpgsqlConnection conn, string who, string note, TextWriter output, TextWriter error)
    {
        if (await ResolveAsync(conn, who) is not { } id) return NotFound(error, who);
        var added = await conn.ExecuteAsync(
            "INSERT INTO agent_allowlist (tenant_id, note) VALUES (@id, @note) ON CONFLICT (tenant_id) DO NOTHING",
            new { id, note });
        output.WriteLine(added == 1
            ? $"account {id} is on the auto-apply allowlist."
            : $"account {id} was already on the auto-apply allowlist.");
        return 0;
    }

    private static async Task<int> DisallowAsync(NpgsqlConnection conn, string who, TextWriter output, TextWriter error)
    {
        if (await ResolveAsync(conn, who) is not { } id) return NotFound(error, who);
        var removed = await conn.ExecuteAsync("DELETE FROM agent_allowlist WHERE tenant_id = @id", new { id });
        output.WriteLine(removed == 1
            ? $"account {id} is off the auto-apply allowlist."
            : $"account {id} was not on the auto-apply allowlist.");
        return 0;
    }

    private static async Task<int> SetStatusAsync(NpgsqlConnection conn, string who, string status, TextWriter output, TextWriter error)
    {
        if (await ResolveAsync(conn, who) is not { } id) return NotFound(error, who);
        await conn.ExecuteAsync("UPDATE users SET status = @status WHERE id = @id", new { id, status });
        output.WriteLine(status == "active"
            ? $"account {id} is enabled."
            : $"account {id} is disabled: signed out on its next request, and skipped by the poller and the agent.");
        return 0;
    }

    /// <summary>An account id, or an email (citext, so any case).</summary>
    private static async Task<long?> ResolveAsync(NpgsqlConnection conn, string who) =>
        long.TryParse(who, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? await conn.ExecuteScalarAsync<long?>("SELECT id FROM users WHERE id = @id", new { id })
            : await conn.ExecuteScalarAsync<long?>("SELECT id FROM users WHERE email = @who::citext", new { who });

    private static int NotFound(TextWriter error, string who)
    {
        error.WriteLine($"admin: no account \"{who}\".");
        return 1;
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string When(DateTime? d) =>
        d is null ? "-" : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture);

    /// <summary>Space-aligned columns: readable, and still one record per line for awk.</summary>
    private static void Table(TextWriter output, string[] header, IEnumerable<string[]> rows)
    {
        var all = rows.Prepend(header).ToList();
        var widths = header.Select((_, i) => all.Max(r => r[i].Length)).ToArray();
        foreach (var r in all)
            output.WriteLine(string.Join("  ", r.Select((c, i) => i == r.Length - 1 ? c : c.PadRight(widths[i]))));
    }
}
