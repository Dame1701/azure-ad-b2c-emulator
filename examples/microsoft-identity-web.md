# Pointing a .NET (Microsoft.Identity.Web) API at the emulator

This is the one piece of wiring you do in **your** app: tell it to validate tokens against
the emulator instead of Azure. For Microsoft.Identity.Web there are two non-obvious knobs,
because the library does not surface them through the `AzureAdB2C` config section.

## 1. Configuration

Point the authority at the emulator (use an environment-specific appsettings overlay so your
committed config stays clean):

```jsonc
{
  "AzureAdB2C": {
    "Instance": "http://localhost:8080/",
    "Domain": "yourtenant.onmicrosoft.com",
    "ClientId": "<your-api-client-id>",     // must equal the emulator's Apis[].Audience
    "SignUpSignInPolicyId": "B2C_1A_SIGNIN"
  }
}
```

## 2. The two knobs (a few lines of startup code)

Microsoft.Identity.Web's B2C issuer validator only accepts Azure-cloud issuers and will
otherwise reject the emulator's issuer with **`IDX40001: Issuer ... does not match any of the
valid issuers`** (which can surface downstream as a misleading `invalid_token ... issuer
'(null)'`). It also requires HTTPS metadata by default. Both are fixed here — guard it so it
only runs in your local/dev environment:

```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Services.Configure<JwtBearerOptions>(
        JwtBearerDefaults.AuthenticationScheme, o =>
        {
            // Allow the http authority used locally.
            o.RequireHttpsMetadata = false;

            // Accept the emulator's fixed issuer: {PublicBaseUrl}/{TenantId}/v2.0/
            o.TokenValidationParameters.ValidIssuer =
                "http://localhost:8080/11111111-1111-1111-1111-111111111111/v2.0/";
        });
}
```

If the emulator's `PublicBaseUrl` or `TenantId` change, update `ValidIssuer` to match.

## Machine-to-machine (client_credentials)

Point your daemon's authority at the emulator and use a `ClientId` that matches one of the
emulator's `Clients[]`. Any secret is accepted unless you set one in config.
