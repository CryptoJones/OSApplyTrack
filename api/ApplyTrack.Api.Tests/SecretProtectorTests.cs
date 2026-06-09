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
        var raw = Convert.FromBase64String(p.Protect("secret"));
        raw[^1] ^= 0xFF; // flip a bit in the GCM tag
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(Convert.ToBase64String(raw)));
    }

    [Fact]
    public void Without_a_master_key_it_is_unavailable_and_refuses_to_protect()
    {
        var p = new SecretProtector(null);
        Assert.False(p.Available);
        Assert.Throws<InvalidOperationException>(() => p.Protect("secret"));
    }
}
