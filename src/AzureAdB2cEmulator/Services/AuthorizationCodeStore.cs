// SPDX-License-Identifier: MIT
// Part of azure-ad-b2c-emulator: a local stand-in for Azure AD B2C.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace AzureAdB2cEmulator.Services;

/// <summary>
///     In-memory store for short-lived authorization codes and refresh tokens. Not
///     durable by design - this is a development-only emulator.
/// </summary>
public sealed class AuthorizationCodeStore
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new();
    private readonly ConcurrentDictionary<string, RefreshToken> _refreshTokens = new();

    public string IssueCode(AuthorizationCode code)
    {
        var key = NewToken();
        _codes[key] = code;
        return key;
    }

    public bool TryRedeemCode(string key, out AuthorizationCode code)
    {
        if (_codes.TryRemove(key, out var stored) && DateTimeOffset.UtcNow - stored.IssuedAt <= CodeLifetime)
        {
            code = stored;
            return true;
        }

        code = default!;
        return false;
    }

    public string IssueRefreshToken(RefreshToken token)
    {
        var key = NewToken();
        _refreshTokens[key] = token;
        return key;
    }

    public bool TryGetRefreshToken(string key, out RefreshToken token)
        => _refreshTokens.TryGetValue(key, out token!);

    private static string NewToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
}

public sealed record AuthorizationCode(
    string ClientId,
    string RedirectUri,
    string UserObjectId,
    string[] Scopes,
    string? Nonce,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    DateTimeOffset IssuedAt,
    string Policy);

public sealed record RefreshToken(
    string ClientId,
    string UserObjectId,
    string[] Scopes,
    string Policy);