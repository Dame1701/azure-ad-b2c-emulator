# azure-ad-b2c-emulator

A small, **standalone** OpenID Connect provider that stands in for **Azure AD B2C** so you
can run your stack locally without an Azure tenant or network access. It serves a B2C-shaped
discovery document and JWKS, an interactive login screen, and a token endpoint supporting the
**authorization-code + PKCE**, **refresh-token** and **client-credentials** grants.

It is **development-only**: no real passwords are checked — any password is accepted for a
known email. Never run it in production.

> **Why it exists.** In real environments a frontend (MSAL.js) signs users in, back-end
> services validate the JWT bearer tokens, and daemons use client-credentials. This emulator
> is a local drop-in for all three, so day-to-day development needs no Azure.

---

## Contents

- [Quick start](#quick-start)
- [Point your app at it](#point-your-app-at-it)
- [Configuration](#configuration)
- [Theming](#theming)
- [How it works](#how-it-works)
- [Limitations](#limitations)
- [Build and run from source](#build-and-run-from-source)
- [Troubleshooting](#troubleshooting)

---

## Quick start

Run the published image — it boots with a sample tenant and two sample users, no config
needed:

```bash
docker run -p 8080:8080 \
  -e Emulator__PublicBaseUrl=http://localhost:8080 \
  ghcr.io/dame1701/azure-ad-b2c-emulator:latest
```

Visit <http://localhost:8080/> for a status page. The sample users are `admin@example.com`
and `user@example.com` (any password). There's a ready-made
[`examples/docker-compose.yml`](examples/docker-compose.yml) too.

---

## Point your app at it

This is the one thing the emulator can't do for you: each app that authenticates has to be
told to use the emulator instead of Azure. It's a small, well-trodden change — ready-made
snippets:

| Your stack | Guide |
| --- | --- |
| .NET API (Microsoft.Identity.Web) | [examples/microsoft-identity-web.md](examples/microsoft-identity-web.md) |
| Frontend SPA (MSAL.js) | [examples/frontend-msal.md](examples/frontend-msal.md) |
| Anything else (generic OIDC/JWT) | [examples/generic-oidc.md](examples/generic-oidc.md) |

The most common trap, for .NET specifically, is Microsoft.Identity.Web rejecting the
emulator's issuer with `IDX40001` — the .NET guide shows the two-line fix.

For a full walkthrough of wiring the emulator into a real application stack — the identity
model, running it in a Kubernetes/kind cluster, adding a dedicated "emulated" environment, and
mounting custom login screens — see the **[Integration guide](docs/integration-guide.md)**.

---

## Configuration

Everything lives under the top-level **`Emulator`** section. The image ships sample defaults
in `appsettings.json`; you override them by either:

- **mounting `appsettings.Local.json`** over `/app/appsettings.Local.json` (loaded
  automatically — best for tenant/clients/users), or
- setting **`Emulator__*` environment variables** (best for single scalars like
  `Emulator__PublicBaseUrl`).

A fully-annotated override template lives at
[`examples/appsettings.Local.example.json`](examples/appsettings.Local.example.json).

```jsonc
{
  "Emulator": {
    // Token issuer, baked into every token as "{PublicBaseUrl}/{TenantId}/v2.0/".
    // Must be the URL your browser/app actually uses.
    "PublicBaseUrl": "http://localhost:8080",

    "Tenant": "contoso.onmicrosoft.com",                 // B2C tenant domain (URL paths)
    "TenantId": "11111111-1111-1111-1111-111111111111",  // GUID used to build the issuer

    "SigningKeyPath": "signing-key.json",                // where the RSA key is persisted
    "AccessTokenLifetimeMinutes": 60,
    "IdTokenLifetimeMinutes": 60,
    "RefreshTokenLifetimeDays": 1,
    "DefaultScope": "access_as_user",                    // scope used if none matches an API
    "SessionCookieName": "emu_session",

    "Branding": {
      "ProductName": "Auth Emulator",                    // wordmark + page title
      "EmulatorTag": "Azure AD B2C Emulator",            // small label by the brand
      "LogoPath": ""                                     // "/assets/logo.png" to use a logo
    },

    // Requested delegated scope is matched to an API by AppIdUri prefix; the matched API's
    // Audience becomes the token's `aud`, and the suffix becomes the short scope.
    "Apis": [
      { "Audience": "<api-client-id>", "AppIdUri": "https://contoso.onmicrosoft.com/api",
        "Scopes": [ "access_as_user" ] }
    ],

    // Confidential clients for the client_credentials (M2M) grant. Blank Secret = any secret.
    "Clients": [
      { "ClientId": "<daemon-client-id>", "Secret": "", "Audience": "<api-client-id>",
        "Scopes": [ "system.access" ] }
    ],

    // Interactive accounts shown in the login dropdown.
    "Users": [
      {
        "ObjectId": "...",                 // emitted as oid/sub/nameidentifier; match your DB
        "Email": "admin@example.com",
        "DisplayName": "Sample Admin",
        "Label": "admin",                  // optional badge in the dropdown
        "Group": "Administrators",         // optional <optgroup>
        "Claims": { "extension_IsAdmin": "true" }  // extra claims emitted verbatim
      }
    ]
  }
}
```

> **Array gotcha.** .NET config merges JSON arrays by **index**, not by appending. In your
> override, declare the **complete** `Apis` / `Clients` / `Users` arrays you want — otherwise
> leftover sample entries can leak through at higher indices.

### Reproducing B2C custom attributes

There's no special "admin" flag — anything app-specific is just a claim. Put your B2C custom
attributes (e.g. `extension_IsAdmin`) in a user's `Claims` map and they're emitted verbatim
on that user's tokens, exactly as B2C would.

---

## Theming

The image ships a neutral default look (a text wordmark, no logo). Because the login page's
chrome, styles and logo are all **files**, you can re-skin it on a pulled image by mounting
your own — no rebuild:

| Mount over | To change |
| --- | --- |
| `/app/wwwroot/assets/styles.css` | colours, fonts, spacing |
| `/app/wwwroot/assets/logo.png` | the logo (also set `Branding.LogoPath` to `/assets/logo.png`) |
| `/app/wwwroot/templates/login-layout.html` | the full page HTML (placeholders: `{{Title}}`, `{{StylesHref}}`, `{{Brand}}`, `{{Tag}}`, `{{Body}}`) |

Brand text and the tag come from the `Branding` config section. Changing the rendering
*logic* (which fields show, the dropdown behaviour) is C# and needs a rebuild — theming
doesn't.

---

## How it works

### Tokens are Azure AD B2C-shaped

Tokens carry `oid` / `sub` / `nameidentifier` (the object id), `emails`, `tfp`, `scp` (and the
long-form `http://schemas.microsoft.com/identity/claims/scope` URI), plus any per-user
`Claims`. Both short B2C names and long-form claim URIs are emitted, so tokens validate
regardless of how your service maps inbound claims.

### One issuer, host-relative endpoints

MSAL.js requires an **https** authority (no `localhost` exemption), but services inside a
cluster can't use a `localhost` URL. The emulator squares this:

- The **issuer is fixed** (`PublicBaseUrl`) so every token carries the same `iss`.
- The discovery document's `jwks_uri` / `token_endpoint` are returned **relative to the host
  the caller used**, so a browser (`https://localhost:8080`) and an in-cluster pod
  (`http://emulator:8080`) each get a reachable URL — while validating against the same issuer.

### Signing

An RSA key is generated on first run and persisted to `signing-key.json` so the JWKS (and
previously issued tokens) survive restarts. In an ephemeral container a fresh key is generated
each start, which is fine — tokens are short-lived.

---

## Limitations

This is a **development convenience**, not a B2C replacement. Know what you're trading away:

- **No security. Development only.** No password is checked — any password is accepted for a
  configured email. It is not hardened and must never be exposed beyond local dev.
- **Single replica only — do not scale it.** The signing key is generated per-pod, and the
  authorization-code / refresh-token stores are in-memory per-pod. Running more than one
  emulator replica behind a Service causes intermittent failures: tokens signed by one replica
  fail JWKS validation against another's key, and an auth code issued by one replica can't be
  redeemed at another. Keep `replicas: 1`. (Multiple *application* pods sharing the one
  emulator is fine — this is only about the emulator's own replica count.)
- **State is in-memory and ephemeral.** Restarting the emulator regenerates the signing key
  (on an ephemeral container filesystem) and drops all in-flight auth codes, refresh tokens and
  sessions. Previously issued tokens then fail validation until clients re-authenticate and
  backends refetch JWKS. Fine day-to-day (tokens are short-lived), but a restart ≈ everyone
  signs in again.
- **It mimics the token shape, not B2C's behaviour.** It reproduces the discovery/JWKS/token
  endpoints, the three grants and the B2C claim shape — but it does **not** execute B2C user
  flows or custom (IEF) policies, MFA, email/password reset, consent, or token revocation. The
  policy name is only inspected for `PASSWORDRESET`; the policy logic itself isn't run. So it
  won't catch bugs that depend on real B2C policy behaviour.
- **You own identity sync.** The object ids you configure must keep matching your user store;
  drift shows up as "user authenticates but isn't found".

For the flows it does cover, tokens validate exactly as real B2C ones — which is what makes it
useful. Just don't mistake it for a conformance or security test of your Azure setup.

---

## Build and run from source

> **This is a bare-bones implementation built around my own particular needs** — a local
> stand-in for Azure AD B2C covering the flows one platform's dev workflow actually uses. It's
> not an exhaustive or conformant B2C emulator (see [Limitations](#limitations)). The full
> source is in this repository — **<https://github.com/Dame1701/azure-ad-b2c-emulator>** — so
> you're encouraged to clone or fork it and adapt it to your own needs.

### Run directly (.NET SDK)

For hacking on the emulator itself:

```bash
dotnet dev-certs https --trust            # first time, so the browser trusts the TLS cert
dotnet run --project src/AzureAdB2cEmulator
# https://localhost:7299 and http://localhost:5299
```

### Build your own local Docker image

You don't need the published image — the repo is fully self-contained, and the multi-stage
`Dockerfile` builds with the .NET SDK *inside* the build, so you only need Docker:

```bash
git clone https://github.com/Dame1701/azure-ad-b2c-emulator
cd azure-ad-b2c-emulator
docker build -t azure-ad-b2c-emulator:dev .
docker run --rm -p 8080:8080 -e Emulator__PublicBaseUrl=http://localhost:8080 \
  azure-ad-b2c-emulator:dev
```

This is the right path if you've **modified the source** (claims, branding logic, the login
template, extra grants) and want to run your version.

**Using your local build in a Kubernetes/kind cluster.** Tag your build with the **same image
reference your deployment expects**, then load it onto the node — a local image of that ref is
used instead of pulling the published one:

```bash
docker build -t ghcr.io/<owner>/azure-ad-b2c-emulator:<tag> .
kind load docker-image ghcr.io/<owner>/azure-ad-b2c-emulator:<tag> -n <cluster>
# with imagePullPolicy: IfNotPresent the loaded image is used; no registry push needed
```

So you can iterate on the emulator entirely locally and only push/publish an image when you
actually want to share it.

---

## Troubleshooting

| Symptom | Cause & fix |
| --- | --- |
| MSAL `authority_uri_insecure` | The authority must be `https`. Serve the emulator over https for the browser and point MSAL at the https URL (trust the dev cert). |
| `IDX40001: Issuer … does not match any of the valid issuers` (.NET) | Microsoft.Identity.Web's B2C validator rejects the custom issuer. Set `TokenValidationParameters.ValidIssuer` — see [examples/microsoft-identity-web.md](examples/microsoft-identity-web.md). |
| `invalid_token … issuer '(null)'` from a downstream | Same root cause as `IDX40001`, surfacing oddly through M2M. Set `ValidIssuer`. |
| `invalid_client` from the token endpoint | The caller's `ClientId` isn't in `Clients[]`, or the secret doesn't match a configured non-blank secret. |
| Login works but the user 401s / isn't found | The user's `ObjectId` doesn't match the id your app keys users on. Align them. |
| Your override's extra users/APIs don't appear, or stale ones remain | Array-by-index merge — declare the complete arrays in your override. |

---

## Licence

[MIT](LICENSE). Contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
