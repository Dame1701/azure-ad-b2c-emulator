// SPDX-License-Identifier: MIT
// Part of azure-ad-b2c-emulator: a local stand-in for Azure AD B2C.

using System.Security.Cryptography;
using System.Text;
using AzureAdB2cEmulator.Configuration;
using AzureAdB2cEmulator.Models;
using AzureAdB2cEmulator.Services;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Convention for consumers: mount an appsettings.Local.json over the baked-in defaults to
// supply your own tenant, clients, APIs and users. Scalars can also be set via Emulator__*
// environment variables.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection(EmulatorOptions.SectionName).Get<EmulatorOptions>()
    ?? new EmulatorOptions());
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<AuthorizationCodeStore>();
builder.Services.AddSingleton<LoginPageRenderer>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

var options = app.Services.GetRequiredService<EmulatorOptions>();
var sessionCookie = options.SessionCookieName;

app.UseCors();
app.UseStaticFiles();

// ---------------------------------------------------------------------------------
// Discovery & JWKS
// ---------------------------------------------------------------------------------

app.MapGet("/{tenant}/{policy}/v2.0/.well-known/openid-configuration", (string tenant, string policy, HttpContext ctx) =>
{
    // Endpoints are advertised relative to the host the caller used, so a browser
    // (http://localhost:8080) and in-cluster pods (http://auth-emulator:8080) each get a
    // URL they can actually reach. The issuer stays fixed (PublicBaseUrl) so every token
    // carries the same iss regardless of which host fetched the metadata.
    var basePath = $"{ctx.Request.Scheme}://{ctx.Request.Host}/{tenant}/{policy}";
    return Results.Json(new
    {
        issuer = options.Issuer,
        authorization_endpoint = $"{basePath}/oauth2/v2.0/authorize",
        token_endpoint = $"{basePath}/oauth2/v2.0/token",
        end_session_endpoint = $"{basePath}/oauth2/v2.0/logout",
        jwks_uri = $"{basePath}/discovery/v2.0/keys",
        response_modes_supported = new[]
        {
            "query",
            "fragment",
            "form_post"
        },
        response_types_supported = new[]
        {
            "code",
            "id_token",
            "code id_token"
        },
        scopes_supported = new[]
        {
            "openid",
            "profile",
            "offline_access"
        },
        subject_types_supported = new[]
        {
            "pairwise"
        },
        id_token_signing_alg_values_supported = new[]
        {
            "RS256"
        },
        token_endpoint_auth_methods_supported = new[]
        {
            "client_secret_post",
            "client_secret_basic"
        },
        grant_types_supported = new[]
        {
            "authorization_code",
            "refresh_token",
            "client_credentials"
        },
        claims_supported = new[]
        {
            "sub",
            "oid",
            "name",
            "emails",
            "tfp",
            "scp"
        }
    });
});

app.MapGet("/{tenant}/{policy}/discovery/v2.0/keys", (SigningKeyProvider keys) =>
    Results.Json(new
    {
        keys = new[]
        {
            keys.GetPublicJsonWebKey()
        }
    }));

// ---------------------------------------------------------------------------------
// Authorize (interactive login)
// ---------------------------------------------------------------------------------

app.MapGet("/{tenant}/{policy}/oauth2/v2.0/authorize", (
    string tenant,
    string policy,
    HttpContext ctx,
    AuthorizationCodeStore codes,
    LoginPageRenderer renderer) =>
{
    var request = ReadAuthorizeRequest(tenant, policy, ctx.Request.Query.ToDictionary(k => k.Key, v => (string?)v.Value));
    var prompt = ctx.Request.Query["prompt"].ToString();
    var sessionUser = ResolveSessionUser(ctx, options, sessionCookie);

    var forceInteractive = prompt is "login" or "select_account";
    if (sessionUser is not null && !forceInteractive)
    {
        return IssueCodeRedirect(request, sessionUser, codes);
    }

    if (prompt == "none")
    {
        // Silent renewal with no session: report back rather than showing UI.
        return RedirectWithError(request, "login_required", "No active emulator session.");
    }

    var isPasswordReset = policy.Contains("PASSWORDRESET", StringComparison.OrdinalIgnoreCase);
    return Results.Content(renderer.RenderSignIn(request, null, isPasswordReset), "text/html");
});

app.MapPost("/{tenant}/{policy}/oauth2/v2.0/authorize", async (
    string tenant,
    string policy,
    HttpContext ctx,
    AuthorizationCodeStore codes,
    LoginPageRenderer renderer) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var request = ReadAuthorizeRequest(tenant, policy, form.ToDictionary(k => k.Key, v => (string?)v.Value));

    var email = form["email"].ToString();
    var user = options.Users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    if (user is null)
    {
        var error = $"No emulator user with email '{email}'. Pick a developer account below.";
        return Results.Content(renderer.RenderSignIn(request, error), "text/html");
    }

    // Over http (local in-cluster) a Secure/SameSite=None cookie would be dropped by the
    // browser, so fall back to Lax+insecure. Over https use None+Secure for iframe SSO.
    var isHttps = ctx.Request.IsHttps;
    ctx.Response.Cookies.Append(sessionCookie, user.ObjectId, new CookieOptions
    {
        HttpOnly = true,
        Secure = isHttps,
        SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
        Path = "/"
    });

    return IssueCodeRedirect(request, user, codes);
});

// ---------------------------------------------------------------------------------
// Token
// ---------------------------------------------------------------------------------

app.MapPost("/{tenant}/{policy}/oauth2/v2.0/token", async (
    string tenant,
    string policy,
    HttpContext ctx,
    AuthorizationCodeStore codes,
    TokenService tokens) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var grantType = form["grant_type"].ToString();

    return grantType switch
    {
        "authorization_code" => ExchangeAuthorizationCode(form, policy, codes, tokens),
        "refresh_token" => ExchangeRefreshToken(form, policy, codes, tokens),
        "client_credentials" => ExchangeClientCredentials(form, policy, tokens),
        _ => Results.BadRequest(new
        {
            error = "unsupported_grant_type",
            error_description = grantType
        })
    };
});

// ---------------------------------------------------------------------------------
// Logout
// ---------------------------------------------------------------------------------

app.MapGet("/{tenant}/{policy}/oauth2/v2.0/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(sessionCookie);
    var postLogout = ctx.Request.Query["post_logout_redirect_uri"].ToString();
    return string.IsNullOrEmpty(postLogout)
        ? Results.Content($"<p>Signed out of the {options.Branding.ProductName}.</p>", "text/html")
        : Results.Redirect(postLogout);
});

// ---------------------------------------------------------------------------------
// Diagnostics
// ---------------------------------------------------------------------------------

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok"
}));

app.MapGet("/", (LoginPageRenderer renderer) =>
{
    var lines = new StringBuilder();
    lines.Append($"Issuer: {options.Issuer}<br/>");
    lines.Append($"Tenant: {options.Tenant}<br/>");
    lines.Append($"Users: {options.Users.Count}, APIs: {options.Apis.Count}, Clients: {options.Clients.Count}");
    return Results.Content(renderer.RenderInfo(options.Branding.ProductName, string.Empty)
        .Replace("<p></p>", $"<p>{lines}</p>"), "text/html");
});

app.Run();
return;

// ---------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------

static AuthorizeRequest ReadAuthorizeRequest(string tenant, string policy, IDictionary<string, string?> values)
{
    string? Get(string key)
    {
        return values.TryGetValue(key, out var v) ? v : null;
    }

    return new AuthorizeRequest
    {
        Tenant = tenant,
        Policy = policy,
        ClientId = Get("client_id") ?? string.Empty,
        RedirectUri = Get("redirect_uri") ?? string.Empty,
        ResponseType = Get("response_type") ?? "code",
        ResponseMode = Get("response_mode") ?? "fragment",
        Scopes = (Get("scope") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
        State = Get("state"),
        Nonce = Get("nonce"),
        CodeChallenge = Get("code_challenge"),
        CodeChallengeMethod = Get("code_challenge_method")
    };
}

IResult IssueCodeRedirect(AuthorizeRequest request, EmulatorUser user, AuthorizationCodeStore codes)
{
    var code = codes.IssueCode(new AuthorizationCode(
        request.ClientId,
        request.RedirectUri,
        user.ObjectId,
        request.Scopes,
        request.Nonce,
        request.CodeChallenge,
        request.CodeChallengeMethod,
        DateTimeOffset.UtcNow,
        request.Policy));

    var parameters = new Dictionary<string, string?>
    {
        ["code"] = code,
        ["state"] = request.State
    };
    return Results.Redirect(BuildRedirect(request.RedirectUri, request.ResponseMode, parameters));
}

static IResult RedirectWithError(AuthorizeRequest request, string error, string description)
{
    var parameters = new Dictionary<string, string?>
    {
        ["error"] = error,
        ["error_description"] = description,
        ["state"] = request.State
    };
    return Results.Redirect(BuildRedirect(request.RedirectUri, request.ResponseMode, parameters));
}

static string BuildRedirect(string redirectUri, string responseMode, IDictionary<string, string?> parameters)
{
    var query = string.Join('&', parameters
        .Where(p => p.Value is not null)
        .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));

    var separator = responseMode == "query" ? '?' : '#';
    return $"{redirectUri}{separator}{query}";
}

static EmulatorUser? ResolveSessionUser(HttpContext ctx, EmulatorOptions options, string cookieName)
{
    var objectId = ctx.Request.Cookies[cookieName];
    return objectId is null ? null : options.Users.FirstOrDefault(u => u.ObjectId == objectId);
}

IResult ExchangeAuthorizationCode(IFormCollection form, string policy, AuthorizationCodeStore codes, TokenService tokens)
{
    var code = form["code"].ToString();
    if (!codes.TryRedeemCode(code, out var stored))
    {
        return Results.BadRequest(new
        {
            error = "invalid_grant",
            error_description = "Unknown or expired code."
        });
    }

    var verifier = form["code_verifier"].ToString();
    if (!VerifyPkce(stored.CodeChallenge, stored.CodeChallengeMethod, verifier))
    {
        return Results.BadRequest(new
        {
            error = "invalid_grant",
            error_description = "PKCE verification failed."
        });
    }

    var user = options.Users.First(u => u.ObjectId == stored.UserObjectId);
    return BuildUserTokenResponse(user, stored.ClientId, stored.Scopes, stored.Nonce, policy, codes, tokens);
}

IResult ExchangeRefreshToken(IFormCollection form, string policy, AuthorizationCodeStore codes, TokenService tokens)
{
    var token = form["refresh_token"].ToString();
    if (!codes.TryGetRefreshToken(token, out var stored))
    {
        return Results.BadRequest(new
        {
            error = "invalid_grant",
            error_description = "Unknown refresh token."
        });
    }

    var user = options.Users.First(u => u.ObjectId == stored.UserObjectId);
    return BuildUserTokenResponse(user, stored.ClientId, stored.Scopes, null, policy, codes, tokens);
}

IResult ExchangeClientCredentials(IFormCollection form, string policy, TokenService tokens)
{
    var clientId = form["client_id"].ToString();
    var secret = form["client_secret"].ToString();

    var client = options.Clients.FirstOrDefault(c => c.ClientId == clientId);
    if (client is null)
    {
        return Results.BadRequest(new
        {
            error = "invalid_client",
            error_description = "Unknown client."
        });
    }

    if (!string.IsNullOrEmpty(client.Secret) && client.Secret != secret)
    {
        return Results.BadRequest(new
        {
            error = "invalid_client",
            error_description = "Invalid client secret."
        });
    }

    var accessToken = tokens.CreateAccessTokenForClient(client, policy);
    var expires = DateTimeOffset.UtcNow.AddMinutes(options.AccessTokenLifetimeMinutes);

    return Results.Json(new Dictionary<string, object?>
    {
        ["token_type"] = "Bearer",
        ["access_token"] = accessToken,
        ["expires_in"] = options.AccessTokenLifetimeMinutes * 60,
        ["ext_expires_in"] = options.AccessTokenLifetimeMinutes * 60,
        ["expires_on"] = expires.ToUnixTimeSeconds().ToString(),
        ["not_before"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        ["scope"] = string.Join(' ', client.Scopes)
    });
}

IResult BuildUserTokenResponse(
    EmulatorUser user,
    string clientId,
    string[] scopes,
    string? nonce,
    string policy,
    AuthorizationCodeStore codes,
    TokenService tokens)
{
    var (audience, shortScopes) = tokens.ResolveDelegatedScopes(scopes);
    var accessToken = tokens.CreateAccessTokenForUser(user, clientId, audience, shortScopes, policy);
    var idToken = tokens.CreateIdToken(user, clientId, nonce, policy);
    var expires = DateTimeOffset.UtcNow.AddMinutes(options.AccessTokenLifetimeMinutes);

    var response = new Dictionary<string, object?>
    {
        ["token_type"] = "Bearer",
        ["access_token"] = accessToken,
        ["id_token"] = idToken,
        ["expires_in"] = options.AccessTokenLifetimeMinutes * 60,
        ["ext_expires_in"] = options.AccessTokenLifetimeMinutes * 60,
        ["expires_on"] = expires.ToUnixTimeSeconds().ToString(),
        ["not_before"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        ["scope"] = string.Join(' ', scopes),
        ["client_info"] = BuildClientInfo(user.ObjectId, options.TenantId)
    };

    if (scopes.Contains("offline_access"))
    {
        response["refresh_token"] = codes.IssueRefreshToken(new RefreshToken(clientId, user.ObjectId, scopes, policy));
    }

    return Results.Json(response);
}

static bool VerifyPkce(string? challenge, string? method, string? verifier)
{
    if (string.IsNullOrEmpty(challenge))
    {
        return true; // No PKCE was requested.
    }

    if (string.IsNullOrEmpty(verifier))
    {
        return false;
    }

    if (string.Equals(method, "S256", StringComparison.OrdinalIgnoreCase))
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncoder.Encode(hash) == challenge;
    }

    return verifier == challenge; // plain
}

static string BuildClientInfo(string objectId, string tenantId)
{
    return Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes($"{{\"uid\":\"{objectId}\",\"utid\":\"{tenantId}\"}}"));
}
