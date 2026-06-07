// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Security.Cryptography;
using System.Text;

namespace ApplyTrack.Api.Auth;

/// <summary>
/// High-entropy opaque tokens for magic links and session ids, plus the sha256 used
/// to persist a magic token by hash (the raw token is emailed, never stored).
/// </summary>
public static class Tokens
{
    /// <summary>A URL-safe base64 string of 32 cryptographically-random bytes.</summary>
    public static string NewOpaque() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Sha256(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
