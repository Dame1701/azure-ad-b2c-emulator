// SPDX-License-Identifier: MIT
// Part of azure-ad-b2c-emulator: a local stand-in for Azure AD B2C.

namespace AzureAdB2cEmulator.Models;

/// <summary>
///     The OAuth2 / OIDC authorization request parameters carried through the login
///     screen. Mirrors the subset of parameters MSAL sends for the auth-code + PKCE flow.
/// </summary>
public sealed record AuthorizeRequest
{
    public required string Tenant { get; init; }
    public required string Policy { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public string ResponseType { get; init; } = "code";
    public string ResponseMode { get; init; } = "fragment";
    public string[] Scopes { get; init; } = [];
    public string? State { get; init; }
    public string? Nonce { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }

    public string ScopeString => string.Join(' ', Scopes);
}
