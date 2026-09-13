// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Security.Cryptography;
using ApplyTrack.Api.Crypto;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Unit tests for the at-rest secret box (AES-256-GCM) that guards a tenant's own LLM
/// API key: round-trips under the right master key, refuses to decrypt under a wrong
/// or tampered one, and degrades to "unavailable" (rather than storing in the clear)
/// when the operator set no master key.
/// </summary>
public class SecretProtectorTests
{
    /// <summary>A pre-1.26 token: bare base64(nonce ‖ ciphertext ‖ tag), no fingerprint.</summary>
    internal static string LegacyToken(string masterSecret, string plaintext)
    {
        var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(masterSecret));
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    [Fact]
    public void A_token_names_its_key_and_bytes_round_trip_with_a_header()
    {
        var p = new SecretProtector("passphrase");
        var token = p.Protect("hello");
        Assert.StartsWith("enc:v1:" + p.Fingerprint + ":", token);
        Assert.True(SecretProtector.IsProtected(token));
        Assert.True(p.IsCurrent(token));
        Assert.False(SecretProtector.IsProtected("hello"));
        Assert.Equal(8, p.Fingerprint.Length);

        var blob = p.ProtectBytes([1, 2, 3, 4, 5]);
        Assert.True(SecretProtector.IsProtected(blob));
        Assert.True(p.IsCurrent(blob));
        Assert.Equal(p.CurrentBytesHeader, blob[..8]);
        Assert.Equal([1, 2, 3, 4, 5], p.UnprotectBytes(blob));
        Assert.False(SecretProtector.IsProtected("%PDF-1.4 plain"u8.ToArray()));
        Assert.ThrowsAny<CryptographicException>(() => p.UnprotectBytes("%PDF-1.4 plain"u8.ToArray()));
    }

    [Fact]
    public void A_previous_key_decrypts_but_never_encrypts_and_an_unknown_key_is_refused()
    {
        var old = new SecretProtector("old");
        var rotated = new SecretProtector("new", ["old"]);
        var token = old.Protect("secret");
        var blob = old.ProtectBytes([9, 9]);

        Assert.Equal("secret", rotated.Unprotect(token));
        Assert.Equal([9, 9], rotated.UnprotectBytes(blob));
        Assert.False(rotated.IsCurrent(token));
        Assert.False(rotated.IsCurrent(blob));
        Assert.True(rotated.IsCurrent(rotated.Protect("again")));
        Assert.NotEqual(old.Fingerprint, rotated.Fingerprint);

        var stranger = new SecretProtector("other");
        var ex = Assert.ThrowsAny<CryptographicException>(() => stranger.Unprotect(token));
        Assert.Contains("does not have", ex.Message);
        Assert.ThrowsAny<CryptographicException>(() => stranger.UnprotectBytes(blob));
    }

    [Fact]
    public void A_legacy_token_still_decrypts_under_the_current_or_a_previous_key()
    {
        var legacy = LegacyToken("old", "sk-legacy");
        Assert.False(SecretProtector.IsProtected(legacy));
        Assert.Equal("sk-legacy", new SecretProtector("old").Unprotect(legacy));
        Assert.Equal("sk-legacy", new SecretProtector("new", ["old"]).Unprotect(legacy));
        Assert.ThrowsAny<CryptographicException>(() => new SecretProtector("new").Unprotect(legacy));
    }

    [Fact]
    public void Round_trips_plaintext_under_the_configured_master_key()
    {
        var p = new SecretProtector("a-long-operator-passphrase");
        Assert.True(p.Available);

        var token = p.Protect("sk-secret-key-123");
        Assert.NotEqual("sk-secret-key-123", token); // ciphertext, not the key
        Assert.Equal("sk-secret-key-123", p.Unprotect(token));
    }

    [Fact]
    public void Each_encryption_uses_a_fresh_nonce_so_ciphertexts_differ()
    {
        var p = new SecretProtector("k");
        Assert.NotEqual(p.Protect("same"), p.Protect("same"));
    }

    [Fact]
    public void A_token_from_a_different_master_key_fails_to_decrypt()
    {
        var token = new SecretProtector("key-one").Protect("secret");
        // AES-GCM throws AuthenticationTagMismatchException, a CryptographicException subtype.
        Assert.ThrowsAny<CryptographicException>(() => new SecretProtector("key-two").Unprotect(token));
    }

    [Fact]
    public void A_tampered_token_fails_to_decrypt()
    {
        var p = new SecretProtector("k");
        var token = p.Protect("secret");
        var head = token[..token.LastIndexOf(':')];
        var raw = Convert.FromBase64String(token[(token.LastIndexOf(':') + 1)..]);
        raw[^1] ^= 0xFF; // flip a bit in the GCM tag
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(head + ":" + Convert.ToBase64String(raw)));
        // A token damaged anywhere else fails the same way, never with a parsing error.
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(token[..^3] + "!!!"));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect("enc:v1:garbage"));
    }

    [Fact]
    public void Without_a_master_key_it_is_unavailable_and_refuses_to_protect()
    {
        var p = new SecretProtector(null);
        Assert.False(p.Available);
        Assert.Throws<InvalidOperationException>(() => p.Protect("secret"));
    }
}
