// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Security.Cryptography;
using System.Text;

namespace ApplyTrack.Api.Crypto;

/// <summary>
/// Authenticated symmetric encryption (AES-256-GCM) for what the app keeps at rest and
/// must not leak from a database file, a dump, or a read-only connection: a tenant's own
/// LLM API key and Telegram bot token, and since 1.26 the résumé PDF, cover letters,
/// packet answers and posting excerpts, and evidence screenshots. The master key comes
/// from operator config (<c>Secrets:Key</c> / <c>APPLYTRACK_SECRETS_KEY</c>) or, with
/// none set, from a key file the app generates on first run (see
/// <see cref="SecretKeySource"/>); any non-empty string is accepted and stretched to 32
/// bytes via SHA-256, so an operator can paste a long random passphrase without base64
/// bookkeeping.
///
/// Two token shapes are read, one is written:
/// <list type="bullet">
/// <item><b>v1 text</b>: <c>enc:v1:&lt;fingerprint&gt;:&lt;base64(nonce ‖ ciphertext ‖ tag)&gt;</c>.
/// The fingerprint names the key, so a rotation sweep can tell which rows still need
/// re-keying without trial decryption.</item>
/// <item><b>v1 bytes</b>: <c>ATK1 ‖ fingerprint ‖ nonce ‖ ciphertext ‖ tag</c>, for bytea columns.</item>
/// <item><b>legacy</b>: bare <c>base64(nonce ‖ ciphertext ‖ tag)</c>, how the API key and bot
/// token were stored before 1.26. Still decrypted, under the current key or a previous one.</item>
/// </list>
/// GCM's tag means a tampered or wrong-key ciphertext fails to decrypt rather than
/// returning garbage. Previous keys (<c>Secrets:PreviousKey</c>) decrypt only; every
/// write uses the current key, which is what makes rotation a restart plus a sweep.
/// </summary>
public sealed class SecretProtector
{
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16;   // AES-GCM standard tag
    private const int FingerprintSize = 4;
    private const string TextPrefix = "enc:v1:";
    private static readonly byte[] Magic = "ATK1"u8.ToArray();

    private sealed record Key(byte[] Bytes, byte[] Fingerprint, string Hex);

    private readonly Key? _current;
    private readonly List<Key> _previous = [];

    public SecretProtector(string? masterSecret, IEnumerable<string>? previousSecrets = null)
    {
        _current = string.IsNullOrEmpty(masterSecret) ? null : Derive(masterSecret);
        foreach (var s in previousSecrets ?? [])
            if (!string.IsNullOrEmpty(s))
                _previous.Add(Derive(s));
    }

    private static Key Derive(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var fp = SHA256.HashData(bytes)[..FingerprintSize];
        return new Key(bytes, fp, Convert.ToHexStringLower(fp));
    }

    /// <summary>Whether a master key is configured (and thus secrets can be stored).</summary>
    public bool Available => _current is not null;

    /// <summary>The current key's fingerprint, as it appears in a v1 text token.</summary>
    public string Fingerprint => _current?.Hex ?? "";

    /// <summary>The <c>LIKE</c> pattern a text column value under the current key matches.</summary>
    public string CurrentTextPattern => TextPrefix + Fingerprint + ":%";

    /// <summary>The header every bytea value under the current key starts with.</summary>
    public byte[] CurrentBytesHeader => _current is null ? [] : [.. Magic, .. _current.Fingerprint];

    /// <summary>True for a v1 text token, whatever key it was written under.</summary>
    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(TextPrefix, StringComparison.Ordinal);

    /// <summary>True for a v1 byte blob, whatever key it was written under.</summary>
    public static bool IsProtected(byte[]? value) =>
        value is not null && value.Length >= Magic.Length + FingerprintSize && value.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    /// <summary>True when the token was written under the current key — nothing to re-key.</summary>
    public bool IsCurrent(string? value) =>
        _current is not null && value is not null && value.StartsWith(TextPrefix + _current.Hex + ":", StringComparison.Ordinal);

    /// <summary>True when the blob was written under the current key — nothing to re-key.</summary>
    public bool IsCurrent(byte[]? value) =>
        _current is not null && IsProtected(value) && value!.AsSpan(Magic.Length, FingerprintSize).SequenceEqual(_current.Fingerprint);

    /// <summary>Encrypt UTF-8 plaintext to a v1 text token. Throws when no master key is set.</summary>
    public string Protect(string plaintext)
    {
        var key = Require();
        return TextPrefix + key.Hex + ":" + Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>Decrypt a v1 or legacy text token. Throws <see cref="CryptographicException"/> on a
    /// tampered, malformed, wrong-key or unknown-key token.</summary>
    public string Unprotect(string token)
    {
        try { return UnprotectCore(token); }
        catch (FormatException ex) { throw new CryptographicException("Malformed ciphertext token.", ex); }
    }

    private string UnprotectCore(string token)
    {
        Require();
        if (IsProtected(token))
        {
            var rest = token.AsSpan(TextPrefix.Length);
            var colon = rest.IndexOf(':');
            if (colon < 0) throw new CryptographicException("Malformed ciphertext token.");
            var key = Find(rest[..colon].ToString())
                ?? throw new CryptographicException("Ciphertext was written under a key this instance does not have.");
            return Encoding.UTF8.GetString(Open(key, Convert.FromBase64String(rest[(colon + 1)..].ToString())));
        }
        // Legacy: no fingerprint, so try the current key and then every previous one.
        var raw = Convert.FromBase64String(token);
        CryptographicException? last = null;
        foreach (var key in AllKeys())
        {
            try { return Encoding.UTF8.GetString(Open(key, raw)); }
            catch (CryptographicException ex) { last = ex; }
        }
        throw last ?? new CryptographicException("No key configured.");
    }

    /// <summary>Encrypt bytes to a v1 blob. Throws when no master key is set.</summary>
    public byte[] ProtectBytes(byte[] plain)
    {
        var key = Require();
        return [.. Magic, .. key.Fingerprint, .. Seal(key, plain)];
    }

    /// <summary>Decrypt a v1 blob. Throws on a tampered, wrong-key, unknown-key or unprotected value.</summary>
    public byte[] UnprotectBytes(byte[] blob)
    {
        Require();
        if (!IsProtected(blob))
            throw new CryptographicException("Not a protected byte blob.");
        var key = Find(Convert.ToHexStringLower(blob.AsSpan(Magic.Length, FingerprintSize)))
            ?? throw new CryptographicException("Ciphertext was written under a key this instance does not have.");
        return Open(key, blob[(Magic.Length + FingerprintSize)..]);
    }

    private Key Require() =>
        _current ?? throw new InvalidOperationException("No master key configured (set APPLYTRACK_SECRETS_KEY).");

    private IEnumerable<Key> AllKeys() => _current is null ? _previous : [_current, .. _previous];

    private Key? Find(string hex) => AllKeys().FirstOrDefault(k => k.Hex == hex);

    private static byte[] Seal(Key key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(key.Bytes, TagSize);
        gcm.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. cipher, .. tag];
    }

    private static byte[] Open(Key key, byte[] raw)
    {
        if (raw.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext token is too short.");
        var nonce = raw.AsSpan(0, NonceSize);
        var cipherLen = raw.Length - NonceSize - TagSize;
        var cipher = raw.AsSpan(NonceSize, cipherLen);
        var tag = raw.AsSpan(NonceSize + cipherLen, TagSize);
        var plain = new byte[cipherLen];
        using var gcm = new AesGcm(key.Bytes, TagSize);
        gcm.Decrypt(nonce, cipher, tag, plain); // throws CryptographicException if invalid
        return plain;
    }
}
