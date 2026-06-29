// SPDX-License-Identifier: MIT
// Part of azure-ad-b2c-emulator: a local stand-in for Azure AD B2C.

using AzureAdB2cEmulator.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AzureAdB2cEmulator.Services;

/// <summary>
///     Mints B2C-shaped access and id tokens. Claims are written via the descriptor's
///     raw claims dictionary so the exact JSON property names are preserved (no .NET
///     claim-type mapping). Both short B2C claim names and the long-form claim URIs a
///     back-end might read are emitted, so tokens validate regardless of how the consuming
///     service configures inbound claim mapping.
/// </summary>
public sealed class TokenService(EmulatorOptions options, SigningKeyProvider signingKeyProvider)
{
    private const string ScopeClaimUri = "http://schemas.microsoft.com/identity/claims/scope";
    private const string NameIdentifierClaimUri = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier";

    private readonly JsonWebTokenHandler _handler = new()
    {
        SetDefaultTimesOnTokenCreation = false
    };

    /// <summary>
    ///     Resolves requested OIDC scope strings (e.g. https://.../api/access_as_user)
    ///     to the API audience and the short scope names placed on the access token.
    /// </summary>
    public (string Audience, string[] ShortScopes) ResolveDelegatedScopes(IEnumerable<string> requestedScopes)
    {
        var shortScopes = new List<string>();
        string? audience = null;

        foreach (var scope in requestedScopes)
        {
            foreach (var api in options.Apis)
            {
                var prefix = api.AppIdUri.TrimEnd('/') + "/";
                if (!scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                audience ??= api.Audience;
                shortScopes.Add(scope[prefix.Length..]);
            }
        }

        // Fall back to the first configured API so a token always has a valid audience.
        audience ??= options.Apis.FirstOrDefault()?.Audience ?? "api";
        if (shortScopes.Count == 0)
        {
            shortScopes.Add(options.DefaultScope);
        }

        return (audience, shortScopes.ToArray());
    }

    public string CreateAccessTokenForUser(EmulatorUser user, string clientId, string audience, string[] shortScopes, string policy)
    {
        var claims = BaseUserClaims(user, policy);
        claims["azp"] = clientId;
        AddScopeClaims(claims, shortScopes);

        return Create(claims, audience, options.AccessTokenLifetimeMinutes);
    }

    public string CreateIdToken(EmulatorUser user, string clientId, string? nonce, string policy)
    {
        var claims = BaseUserClaims(user, policy);
        if (!string.IsNullOrEmpty(nonce))
        {
            claims["nonce"] = nonce;
        }

        return Create(claims, clientId, options.IdTokenLifetimeMinutes);
    }

    public string CreateAccessTokenForClient(EmulatorClient client, string policy)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = client.ClientId,
            ["azp"] = client.ClientId,
            ["ver"] = "1.0",
            ["tfp"] = policy
        };
        AddScopeClaims(claims, client.Scopes.ToArray());

        return Create(claims, client.Audience, options.AccessTokenLifetimeMinutes);
    }

    private Dictionary<string, object> BaseUserClaims(EmulatorUser user, string policy)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.ObjectId,
            ["oid"] = user.ObjectId,
            [NameIdentifierClaimUri] = user.ObjectId,
            ["name"] = user.DisplayName,
            ["emails"] = new[]
            {
                user.Email
            },
            ["tfp"] = policy,
            ["ver"] = "1.0"
        };

        if (!string.IsNullOrWhiteSpace(user.GivenName))
        {
            claims["given_name"] = user.GivenName;
        }

        if (!string.IsNullOrWhiteSpace(user.FamilyName))
        {
            claims["family_name"] = user.FamilyName;
        }

        // Extra per-user claims, emitted verbatim. This is how B2C custom attributes such
        // as extension_IsAdmin are reproduced - put them in the user's Claims map in config.
        foreach (var (name, value) in user.Claims)
        {
            claims[name] = value;
        }

        return claims;
    }

    private static void AddScopeClaims(Dictionary<string, object> claims, string[] shortScopes)
    {
        claims["scp"] = string.Join(' ', shortScopes);
        // Long-form claim some back-ends read directly (system.access / access_as_user etc).
        claims[ScopeClaimUri] = shortScopes;
    }

    private string Create(Dictionary<string, object> claims, string audience, int lifetimeMinutes)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(lifetimeMinutes),
            SigningCredentials = signingKeyProvider.SigningCredentials,
            Claims = claims
        };

        return _handler.CreateToken(descriptor);
    }
}
