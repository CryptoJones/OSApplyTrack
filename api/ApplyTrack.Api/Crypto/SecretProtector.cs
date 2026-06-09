// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Security.Cryptography;
using System.Text;

namespace ApplyTrack.Api.Crypto;

/// <summary>
/// Authenticated symmetric encryption (AES-256-GCM) for the small, sensitive
/// secrets the app stores at rest — currently a tenant's own LLM API key. The
/// master key comes from operator config (<c>Secrets:Key</c> / the
/// <c>APPLYTRACK_SECRETS_KEY</c> env var); any non-empty string is accepted and
/// stretched to 32 bytes via SHA-256, so an operator can paste a long random
/// passphrase without base64 bookkeeping.
///
/// When no master key is configured, <see cref="Available"/> is false and the API
/// refuses to store per-tenant keys (the instance-default endpoint still works).
/// The token format is base64(nonce ‖ ciphertext ‖ tag); GCM's tag means a tampered
/// or wrong-key ciphertext fails to decrypt rather than returning garbage.
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // AES-GCM standard tag

    private readonly byte[]? _key;

    public SecretProtector(string? masterSecret) =>
        _key = string.IsNullOrEmpty(masterSecret)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(masterSecret));

    /// <summary>Whether a master key is configured (and thus secrets can be stored).</summary>
    public bool Available => _key is not null;

    /// <summary>Encrypt UTF-8 plaintext to a base64 token. Throws when no master key is set.</summary>
    public string Protect(string plaintext)
    {
        if (_key is null)
            throw new InvalidOperationException("No master key configured (set APPLYTRACK_SECRETS_KEY).");

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipher, tag);

        var token = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, token, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, token, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, token, NonceSize + cipher.Length, TagSize);
        return Convert.ToBase64String(token);
    }

    /// <summary>Decrypt a base64 token back to plaintext. Throws on a tampered/wrong-key token.</summary>
    public string Unprotect(string token)
    {
        if (_key is null)
            throw new InvalidOperationException("No master key configured (set APPLYTRACK_SECRETS_KEY).");

        var raw = Convert.FromBase64String(token);
        if (raw.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext token is too short.");

        var nonce = raw.AsSpan(0, NonceSize);
        var cipherLen = raw.Length - NonceSize - TagSize;
        var cipher = raw.AsSpan(NonceSize, cipherLen);
        var tag = raw.AsSpan(NonceSize + cipherLen, TagSize);
        var plain = new byte[cipherLen];

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException if invalid
        return Encoding.UTF8.GetString(plain);
    }
}
