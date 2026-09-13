// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Security.Cryptography;

namespace ApplyTrack.Api.Crypto;

/// <summary>Where the master key came from, for the startup log and for tests.</summary>
public sealed record ResolvedSecretKey(string Key, IReadOnlyList<string> Previous, string Source);

/// <summary>
/// Resolves the master key every deploy encrypts with — <b>secure by default</b>. In order:
/// <list type="number">
/// <item><c>Secrets:Key</c> / <c>APPLYTRACK_SECRETS_KEY</c>: the operator manages the key
/// (the hardened production stack requires this).</item>
/// <item>A key file (<c>Secrets:KeyFile</c> / <c>APPLYTRACK_SECRETS_KEY_FILE</c>, default
/// <c>/var/lib/applytrack/secrets.key</c> in a container, else the user's local app-data
/// folder), read if present and <b>generated on first run</b> if not.</item>
/// </list>
/// The app refuses to start when neither works: the alternative — a self-hoster who never
/// set a variable running a plaintext database with no signal that anything was missing —
/// is exactly what this replaces. A generated key lives on the same disk as the data, which
/// stops an offline database copy but not someone with the whole box; operators who want
/// better manage the key themselves. Losing the key means losing what it encrypts, so the
/// key file must be backed up with the database.
///
/// <c>Secrets:PreviousKey</c> / <c>APPLYTRACK_SECRETS_KEY_PREVIOUS</c> (one or more,
/// separated by <c>;</c>) is how the key is rotated: set the new key as current and the old
/// one as previous, restart, and the startup sweep re-keys every row; then drop the previous.
/// </summary>
public static class SecretKeySource
{
    public const string DefaultContainerPath = "/var/lib/applytrack/secrets.key";

    public static ResolvedSecretKey Resolve(IConfiguration config)
    {
        var previous = (config["Secrets:PreviousKey"] ?? config["APPLYTRACK_SECRETS_KEY_PREVIOUS"] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var configured = config["Secrets:Key"] ?? config["APPLYTRACK_SECRETS_KEY"];
        if (!string.IsNullOrWhiteSpace(configured))
            return new ResolvedSecretKey(configured.Trim(), previous, "configuration (APPLYTRACK_SECRETS_KEY)");

        var path = config["Secrets:KeyFile"] ?? config["APPLYTRACK_SECRETS_KEY_FILE"];
        if (string.IsNullOrWhiteSpace(path))
            path = DefaultPath();
        path = Path.GetFullPath(path.Trim());

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length > 0)
                    return new ResolvedSecretKey(existing, previous, $"key file {path}");
            }
            // In a container, generate only onto storage that outlives the container. A key
            // written into the image's writable layer disappears with the next recreate and
            // takes every sealed row with it — refusing to start is the far better outcome.
            var dir = Path.GetDirectoryName(path)!;
            if (InContainer() && !IsMountPoint(dir))
                throw new InvalidOperationException(
                    $"No encryption key: APPLYTRACK_SECRETS_KEY is unset and {dir} is not a mounted volume, so a generated key "
                    + "would be lost when the container is recreated (and the data sealed under it with it). Set APPLYTRACK_SECRETS_KEY "
                    + $"to a long random value, or mount a volume at {dir} (the compose files and quadlets ship one).");
            var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            Directory.CreateDirectory(dir);
            // Owner-only from the first byte: create, then restrict, then write.
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var w = new StreamWriter(fs);
                w.Write(generated);
                w.Write('\n');
            }
            return new ResolvedSecretKey(generated, previous, $"generated at {path} — back this file up with the database; losing it loses the encrypted data");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"No encryption key: APPLYTRACK_SECRETS_KEY is unset and the key file {path} could not be read or created "
                + $"({ex.Message}). Set APPLYTRACK_SECRETS_KEY to a long random value, or mount a writable volume at "
                + $"{Path.GetDirectoryName(path)} (or point APPLYTRACK_SECRETS_KEY_FILE somewhere writable) so one can be generated.", ex);
        }
    }

    private static bool InContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is the directory (or its nearest existing ancestor) a mount point? Read from
    /// /proc/self/mountinfo, whose 5th field is the mount's path inside this namespace.</summary>
    internal static bool IsMountPoint(string dir)
    {
        try
        {
            if (!File.Exists("/proc/self/mountinfo")) return false;
            var full = Path.GetFullPath(dir).TrimEnd('/');
            var mounts = File.ReadLines("/proc/self/mountinfo")
                .Select(l => l.Split(' '))
                .Where(f => f.Length > 4)
                .Select(f => f[4].Replace("\\040", " ").TrimEnd('/'))
                .ToHashSet(StringComparer.Ordinal);
            // A volume mounted at the key directory, or at a parent of it (but never just "/").
            for (var d = full; d.Length > 1; d = Path.GetDirectoryName(d)?.TrimEnd('/') ?? "")
                if (mounts.Contains(d)) return true;
            return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string DefaultPath()
    {
        if (InContainer())
            return DefaultContainerPath;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(local, "applytrack", "secrets.key");
    }
}
