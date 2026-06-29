// SPDX-License-Identifier: MIT
// Part of azure-adb2c-emulator: a local stand-in for Azure AD B2C.

namespace AzureAdB2cEmulator.Configuration;

/// <summary>
///     Root configuration for the local Azure AD B2C emulator. Bound from the
///     "Emulator" configuration section. Every value can be overridden by mounting an
///     <c>appsettings.Local.json</c> file or via <c>Emulator__*</c> environment variables.
/// </summary>
public sealed class EmulatorOptions
{
    public const string SectionName = "Emulator";

    /// <summary>
    ///     The externally visible base URL of the emulator, e.g. https://localhost:7299.
    ///     This MUST be identical for every caller (browser and back-end services), as it
    ///     forms the token issuer and all advertised OIDC endpoints. If browser and pods
    ///     reach the emulator on different URLs, issuer validation will fail.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "https://localhost:7299";

    /// <summary>The B2C tenant domain, e.g. contoso.onmicrosoft.com.</summary>
    public string Tenant { get; set; } = "contoso.onmicrosoft.com";

    /// <summary>The tenant GUID used to build the B2C-style issuer value.</summary>
    public string TenantId { get; set; } = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    ///     File the RSA signing key is persisted to, so JWKS / token signatures remain
    ///     stable across restarts. Relative paths are resolved against the content root.
    /// </summary>
    public string SigningKeyPath { get; set; } = "signing-key.json";

    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int IdTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 1;

    /// <summary>
    ///     The short scope name placed on an access token when the requested delegated
    ///     scopes don't match any configured API. Keeps tokens from ever lacking a scope.
    /// </summary>
    public string DefaultScope { get; set; } = "access_as_user";

    /// <summary>Name of the emulator's interactive session cookie.</summary>
    public string SessionCookieName { get; set; } = "emu_session";

    /// <summary>Branding shown on the login / status pages.</summary>
    public BrandingOptions Branding { get; set; } = new();

    /// <summary>Seeded interactive users available on the login screen.</summary>
    public List<EmulatorUser> Users { get; set; } = [];

    /// <summary>
    ///     The protected APIs. Determines the audience and short scope names placed on
    ///     access tokens when a matching delegated scope is requested.
    /// </summary>
    public List<EmulatorApi> Apis { get; set; } = [];

    /// <summary>Confidential clients permitted to use the client_credentials grant.</summary>
    public List<EmulatorClient> Clients { get; set; } = [];

    public string Issuer => $"{PublicBaseUrl.TrimEnd('/')}/{TenantId}/v2.0/";
}

public sealed class BrandingOptions
{
    /// <summary>Product name shown in the page title and as the wordmark when no logo is set.</summary>
    public string ProductName { get; set; } = "Auth Emulator";

    /// <summary>Small label shown next to the brand, e.g. "Azure AD B2C Emulator".</summary>
    public string EmulatorTag { get; set; } = "Azure AD B2C Emulator";

    /// <summary>
    ///     Optional URL/path to a logo image (e.g. "/assets/logo.png"). Mount your own file
    ///     over wwwroot/assets to use it. When empty, the ProductName is shown as text.
    /// </summary>
    public string? LogoPath { get; set; }
}

public sealed class EmulatorUser
{
    /// <summary>The AAD object id. MUST match the id your application keys users on.</summary>
    public string ObjectId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }

    /// <summary>
    ///     Extra claims emitted verbatim on this user's tokens, e.g. a B2C custom attribute
    ///     such as { "extension_IsAdmin": "true" }. The key is the exact JSON claim name.
    /// </summary>
    public Dictionary<string, string> Claims { get; set; } = [];

    /// <summary>Optional short badge shown after the name in the login dropdown.</summary>
    public string? Label { get; set; }

    /// <summary>Optional grouping label, purely to organise the login screen.</summary>
    public string? Group { get; set; }
}

public sealed class EmulatorApi
{
    /// <summary>The audience placed on access tokens (the API app registration's client id).</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The App ID URI prefix, e.g. https://contoso.onmicrosoft.com/api.</summary>
    public string AppIdUri { get; set; } = string.Empty;

    /// <summary>Short scope names this API exposes, e.g. access_as_user.</summary>
    public List<string> Scopes { get; set; } = [];
}

public sealed class EmulatorClient
{
    public string ClientId { get; set; } = string.Empty;

    /// <summary>If null or empty, any secret is accepted (dev convenience).</summary>
    public string? Secret { get; set; }

    /// <summary>The audience placed on the issued access token.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    ///     Scope claim values granted to this client, e.g. ["system.access"]. These are
    ///     emitted as the scope claim a back-end reads to recognise a system daemon.
    /// </summary>
    public List<string> Scopes { get; set; } = [];
}
