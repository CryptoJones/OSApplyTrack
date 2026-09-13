// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Crypto;
using Microsoft.Extensions.Configuration;

namespace ApplyTrack.Api.Tests;

/// <summary>Secure by default: a configured key wins, else a key file is read or generated
/// owner-only, and a deploy that can do neither refuses to start with instructions.</summary>
public class SecretKeySourceTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void A_configured_key_wins_and_previous_keys_are_split()
    {
        var r = SecretKeySource.Resolve(Config(("Secrets:Key", " k1 "), ("Secrets:PreviousKey", "old1; old2 ;")));
        Assert.Equal("k1", r.Key);
        Assert.Equal(["old1", "old2"], r.Previous);
        Assert.Contains("APPLYTRACK_SECRETS_KEY", r.Source);

        var env = SecretKeySource.Resolve(Config(("APPLYTRACK_SECRETS_KEY", "k2")));
        Assert.Equal("k2", env.Key);
    }

    [Fact]
    public void With_no_key_configured_one_is_generated_once_kept_owner_only_and_read_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "applytrack-keytest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "nested", "secrets.key");
        try
        {
            var first = SecretKeySource.Resolve(Config(("Secrets:Key", ""), ("Secrets:KeyFile", path)));
            Assert.True(File.Exists(path));
            Assert.Contains("generated", first.Source);
            Assert.True(first.Key.Length >= 60); // 48 random bytes, base64
            Assert.Equal(first.Key, File.ReadAllText(path).Trim());
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            var second = SecretKeySource.Resolve(Config(("APPLYTRACK_SECRETS_KEY_FILE", path)));
            Assert.Equal(first.Key, second.Key);
            Assert.Contains("key file", second.Source);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_unwritable_key_file_refuses_to_start_with_instructions()
    {
        // A path under a regular FILE cannot be created: the directory creation fails.
        var blocker = Path.GetTempFileName();
        try
        {
            var path = Path.Combine(blocker, "secrets.key");
            var ex = Assert.Throws<InvalidOperationException>(() => SecretKeySource.Resolve(Config(("Secrets:KeyFile", path))));
            Assert.Contains("APPLYTRACK_SECRETS_KEY", ex.Message);
            Assert.Contains("volume", ex.Message);
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
