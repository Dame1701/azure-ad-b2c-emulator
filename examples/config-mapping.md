# Worked example: repointing your app config at the emulator

This shows, side by side, a typical application's auth configuration **before** (pointing at
real Azure AD B2C) and **after** (pointing at the emulator) — for a backend API, a
machine-to-machine daemon, and a frontend SPA — and how each id maps to the emulator's own
config.

> **You don't invent any ids.** The emulator config is just a **restatement of your existing
> app registrations** — the same tenant id, API audience, client ids and user object ids you
> already use with real Azure AD B2C. Reuse them all, **including your tenant id**. That's what
> makes emulator tokens look identical to production ones. The only things that change when you
> point an app at the emulator are the **authority host** and two .NET knobs.

## Placeholders used below

Substitute your own real values everywhere you see `<…>`. The example column shows the *shape*
only — use your actual ids, which you already have in your Azure AD B2C setup.

| Placeholder | What it is — and where you already have it | Example (shape only) |
| --- | --- | --- |
| `<TenantId>` | your Azure AD B2C **tenant id** | `11111111-1111-1111-1111-111111111111` |
| `<TenantDomain>` | your tenant **domain** | `yourtenant.onmicrosoft.com` |
| `<ApiClientId>` | your **API** app registration's client id (the token **audience**) | `22222222-2222-2222-2222-222222222222` |
| `<SpaClientId>` | your **SPA** app registration's client id | `33333333-3333-3333-3333-333333333333` |
| `<DaemonClientId>` | your **daemon / M2M** app registration's client id | `44444444-4444-4444-4444-444444444444` |

Every one of these comes straight from your existing config — copy them across unchanged.

## The emulator config that backs this example

Notice there are **no new values here** — every id is one you already use:

```jsonc
"Emulator": {
  "PublicBaseUrl": "https://localhost:8080",   // browser-facing URL (or set via Emulator__PublicBaseUrl)
  "Tenant":   "<TenantDomain>",                // your real tenant domain
  "TenantId": "<TenantId>",                    // your REAL Azure tenant id — reuse it, don't invent one
  "Apis": [
    { "Audience": "<ApiClientId>",             // = your API app's client id
      "AppIdUri": "https://<TenantDomain>/api",
      "Scopes":   [ "access_as_user" ] }
  ],
  "Clients": [
    { "ClientId": "<DaemonClientId>", "Secret": "",   // = your daemon app's client id
      "Audience": "<ApiClientId>", "Scopes": [ "system.access" ] }
  ],
  "Users": [ /* your users — object ids must match your store */ ]
}
```

---

## The issuer — built from `PublicBaseUrl` + `TenantId`

Every token's `iss` is **fixed** and built from two emulator settings:

```
{PublicBaseUrl}/{TenantId}/v2.0/
```

So with your real tenant id it becomes (example shape):

```
https://localhost:8080/<TenantId>/v2.0/
   e.g. https://localhost:8080/11111111-1111-1111-1111-111111111111/v2.0/
```

That exact string is what your backend must accept as **`ValidIssuer`** (next section). The
emulator's *discovery endpoints* adapt to whatever host the caller used, but the `iss` is always
this fixed value — so it's the one thing every consumer must agree on.

**To change it**, edit the emulator's `PublicBaseUrl` and/or `TenantId`, then update your
backend's `ValidIssuer` to match **byte-for-byte** (scheme, host, port, the tenant id, and the
trailing `/v2.0/`).

> `PublicBaseUrl` is the **browser-facing** URL (`https://localhost:8080`), even though your
> pods reach the emulator on a different in-cluster host (`http://auth-emulator:8080`). The
> issuer is always `PublicBaseUrl`; the authority your backend points at can differ.

---

## Backend API (Microsoft.Identity.Web) — `AzureAdB2C` section

**Before (real Azure AD B2C):**
```jsonc
"AzureAdB2C": {
  "Instance":              "https://<TenantDomain-prefix>.b2clogin.com/",
  "Domain":                "<TenantDomain>",
  "ClientId":              "<ApiClientId>",
  "SignUpSignInPolicyId":  "B2C_1A_SIGNIN"
}
```

**After (emulator — in `appsettings.DevelopmentEmulator.json`):**
```jsonc
"AzureAdB2C": {
  "Instance":              "http://auth-emulator:8080/",   // ← authority: the in-cluster URL
  "Domain":                "<TenantDomain>",               //   unchanged
  "ClientId":              "<ApiClientId>",                //   unchanged (= Apis[].Audience)
  "SignUpSignInPolicyId":  "B2C_1A_SIGNIN",                //   unchanged
  "ValidIssuer":           "https://localhost:8080/<TenantId>/v2.0/", // ← the fixed issuer, NOT the Instance
  "RequireHttpsMetadata":  false                           // ← the in-cluster authority is http
}
```

Plus the startup snippet that applies the last two (Microsoft.Identity.Web doesn't honour them
from config on its own) — see [microsoft-identity-web.md](microsoft-identity-web.md).

> **What changed:** only `Instance` (authority host) and the two emulator knobs. `ClientId`,
> `Domain` and the policy are identical to production. `ClientId` here must equal the emulator's
> `Apis[].Audience` — it's the `aud` the backend expects on tokens.

---

## Machine-to-machine daemon — e.g. an `AzureAdM2M` section

**Before:**
```jsonc
"AzureAdM2M": {
  "Authority":    "https://<TenantDomain-prefix>.b2clogin.com/<TenantDomain>/B2C_1A_SIGNIN",
  "ClientId":     "<DaemonClientId>",
  "ClientSecret": "<real-secret>",
  "Scope":        "https://<TenantDomain>/api/.default"
}
```

**After (emulator):**
```jsonc
"AzureAdM2M": {
  "Authority":    "http://auth-emulator:8080/<TenantDomain>/B2C_1A_SIGNIN",  // ← host only
  "ClientId":     "<DaemonClientId>",     // unchanged; must match Clients[].ClientId
  "ClientSecret": "anything",             // emulator accepts any secret if Secret is blank
  "Scope":        "https://<TenantDomain>/api/.default"   // unchanged
}
```

> **What changed:** only the authority host (and the secret can be a throwaway). The `ClientId`
> must exist in the emulator's `Clients[]`, which decides the scopes the issued token carries.

---

## Frontend SPA (MSAL.js) — `auth` section

**Before:**
```jsonc
"auth": {
  "clientId":         "<SpaClientId>",
  "authority":        "https://<TenantDomain-prefix>.b2clogin.com/<TenantDomain>/B2C_1A_SIGNIN",
  "knownAuthorities": [ "<TenantDomain-prefix>.b2clogin.com" ],
  "redirectUri":      "http://localhost:4200"
}
```

**After (emulator):**
```jsonc
"auth": {
  "clientId":         "<SpaClientId>",     // unchanged
  "authority":        "https://localhost:8080/<TenantDomain>/B2C_1A_SIGNIN",  // ← browser URL (https)
  "knownAuthorities": [ "localhost:8080" ],                                   // ← the emulator host
  "redirectUri":      "http://localhost:4200"                                 //   unchanged
}
```

> **What changed:** only `authority` and `knownAuthorities`. The SPA uses the **https browser
> URL**, whereas the backend uses the **http in-cluster URL** — same emulator, different
> reachability (see the [integration guide](../docs/integration-guide.md#3-running-the-emulator-inside-your-cluster)).

---

## How the ids map across

| Id | In your app config | In the emulator config | Must match? |
| --- | --- | --- | --- |
| `<ApiClientId>` | backend `AzureAdB2C:ClientId` | `Apis[].Audience` | **Yes** — it's the token `aud` the API validates |
| App ID URI + scope | the scope your SPA/daemon requests (`…/api/access_as_user`) | `Apis[].AppIdUri` + `Scopes[]` | **Yes** — the emulator matches a requested scope to an API by AppIdUri prefix |
| `<DaemonClientId>` | `…M2M:ClientId` | `Clients[].ClientId` | **Yes** — identifies the M2M caller |
| `<SpaClientId>` | frontend `auth.clientId` | *(not needed)* | No — the emulator echoes it as the `id_token` audience; it doesn't validate it |
| `<TenantId>` | backend `ValidIssuer` (inside the issuer string) | `TenantId` (+ `PublicBaseUrl`) | **Yes** — together they form the fixed issuer `{PublicBaseUrl}/{TenantId}/v2.0/`, which `ValidIssuer` must equal exactly |
| Policy `B2C_1A_SIGNIN` | the authority's policy path segment | the `{policy}` URL segment | any name works; a name containing `PASSWORDRESET` renders the reset screen |

### The short version

- **Reuse your existing ids — all of them, including `<TenantId>`.** Copy them from your current
  Azure AD B2C config into the emulator's `TenantId` / `Apis` / `Clients` / `Users`. You invent
  nothing, so emulated tokens look exactly like production ones.
- **Only the authority host changes** in your app config (`b2clogin.com` → the emulator), plus
  the two .NET knobs (`RequireHttpsMetadata: false` and an explicit `ValidIssuer`).
- The **SPA client id** doesn't need to be in the emulator config; the **API audience** and
  **daemon client id** do.
