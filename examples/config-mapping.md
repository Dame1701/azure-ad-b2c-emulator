# Worked example: repointing your app config at the emulator

This shows, side by side, a typical application's auth configuration **before** (pointing at
real Azure AD B2C) and **after** (pointing at the emulator) — for a backend API, a
machine-to-machine daemon, and a frontend SPA — and how the ids line up with the emulator's
own config.

The headline: **you keep your real app-registration ids.** The only things that change are the
**authority host** and two .NET-specific knobs. You then **mirror the API audience and any
daemon client id into the emulator's config** so it knows what to issue and accept.

## Example ids used throughout

| Thing | Example value |
| --- | --- |
| Tenant domain | `yourtenant.onmicrosoft.com` |
| Tenant id | `11111111-1111-1111-1111-111111111111` |
| **API** app registration (client id) | `22222222-2222-2222-2222-222222222222` |
| **SPA** app registration (client id) | `33333333-3333-3333-3333-333333333333` |
| **Daemon** app registration (client id) | `44444444-4444-4444-4444-444444444444` |
| API App ID URI | `https://yourtenant.onmicrosoft.com/api` |
| Sign-in policy | `B2C_1A_SIGNIN` |

## The emulator config that backs this example

```jsonc
"Emulator": {
  "PublicBaseUrl": "https://localhost:8080",              // the browser-facing URL (or set via
                                                          //   Emulator__PublicBaseUrl env var)
  "Tenant":   "yourtenant.onmicrosoft.com",
  "TenantId": "11111111-1111-1111-1111-111111111111",
  "Apis": [
    { "Audience": "22222222-2222-2222-2222-222222222222",   // = your API app's client id
      "AppIdUri": "https://yourtenant.onmicrosoft.com/api",
      "Scopes":   [ "access_as_user" ] }
  ],
  "Clients": [
    { "ClientId": "44444444-4444-4444-4444-444444444444",   // = your daemon app's client id
      "Secret":   "",                                       // blank = any secret accepted
      "Audience": "22222222-2222-2222-2222-222222222222",   // token aud = the API
      "Scopes":   [ "system.access" ] }
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

With this example's values (`PublicBaseUrl = https://localhost:8080`,
`TenantId = 11111111-1111-1111-1111-111111111111`) the issuer is:

```
https://localhost:8080/11111111-1111-1111-1111-111111111111/v2.0/
```

That exact string is what your backend must accept as **`ValidIssuer`** (next section). The
emulator's *discovery endpoints* adapt to whatever host the caller used, but the `iss` is always
this fixed value — so it's the one thing every consumer must agree on.

**To change it**, edit the emulator's `PublicBaseUrl` and/or `TenantId`, then update your
backend's `ValidIssuer` to match **byte-for-byte** (scheme, host, port, the tenant GUID, and the
trailing `/v2.0/`). For example, switching to tenant id `aaaa…` on port `9000`:

```
PublicBaseUrl = https://localhost:9000
TenantId      = aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa
⇒ issuer / ValidIssuer = https://localhost:9000/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/v2.0/
```

> Note `PublicBaseUrl` is the **browser-facing** URL (`https://localhost:8080`), even though
> your pods reach the emulator on a different in-cluster host (`http://auth-emulator:8080`). The
> issuer is always `PublicBaseUrl`; the authority your backend points at can differ.

---

## Backend API (Microsoft.Identity.Web) — `AzureAdB2C` section

**Before (real Azure AD B2C):**
```jsonc
"AzureAdB2C": {
  "Instance":              "https://yourtenant.b2clogin.com/",
  "Domain":                "yourtenant.onmicrosoft.com",
  "ClientId":              "22222222-2222-2222-2222-222222222222",
  "SignUpSignInPolicyId":  "B2C_1A_SIGNIN"
}
```

**After (emulator — in `appsettings.DevelopmentEmulator.json`):**
```jsonc
"AzureAdB2C": {
  "Instance":              "http://auth-emulator:8080/",          // ← authority: the in-cluster URL
  "Domain":                "yourtenant.onmicrosoft.com",          //   unchanged
  "ClientId":              "22222222-2222-2222-2222-222222222222",//   unchanged
  "SignUpSignInPolicyId":  "B2C_1A_SIGNIN",                       //   unchanged
  "ValidIssuer":           "https://localhost:8080/11111111-1111-1111-1111-111111111111/v2.0/", // ← = the fixed issuer (PublicBaseUrl + TenantId), NOT the Instance above
  "RequireHttpsMetadata":  false                                  // ← the in-cluster authority is http
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
  "Authority":    "https://yourtenant.b2clogin.com/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN",
  "ClientId":     "44444444-4444-4444-4444-444444444444",
  "ClientSecret": "<real-secret>",
  "Scope":        "https://yourtenant.onmicrosoft.com/api/.default"
}
```

**After (emulator):**
```jsonc
"AzureAdM2M": {
  "Authority":    "http://auth-emulator:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN", // ← host
  "ClientId":     "44444444-4444-4444-4444-444444444444",   // must match Clients[].ClientId
  "ClientSecret": "anything",                               // emulator accepts any secret if Secret is blank
  "Scope":        "https://yourtenant.onmicrosoft.com/api/.default"  // unchanged
}
```

> **What changed:** only the authority host (and the secret can be a throwaway). The `ClientId`
> must exist in the emulator's `Clients[]`, which decides the scopes the issued token carries.

---

## Frontend SPA (MSAL.js) — `auth` section

**Before:**
```jsonc
"auth": {
  "clientId":         "33333333-3333-3333-3333-333333333333",
  "authority":        "https://yourtenant.b2clogin.com/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN",
  "knownAuthorities": [ "yourtenant.b2clogin.com" ],
  "redirectUri":      "http://localhost:4200"
}
```

**After (emulator):**
```jsonc
"auth": {
  "clientId":         "33333333-3333-3333-3333-333333333333",      // unchanged
  "authority":        "https://localhost:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN", // ← browser URL (https)
  "knownAuthorities": [ "localhost:8080" ],                        // ← the emulator host
  "redirectUri":      "http://localhost:4200"                      //   unchanged
}
```

> **What changed:** only `authority` and `knownAuthorities`. Note the SPA uses the **https
> browser URL**, whereas the backend uses the **http in-cluster URL** — same emulator, different
> reachability (see the [integration guide](../docs/integration-guide.md#3-running-the-emulator-inside-your-cluster)).

---

## How the ids map across

| Example id | In your app config | In the emulator config | Must match? |
| --- | --- | --- | --- |
| API `22222222…` | backend `AzureAdB2C:ClientId` | `Apis[].Audience` | **Yes** — it's the token `aud` the API validates |
| App ID URI + scope | the scope your SPA/daemon requests (`…/api/access_as_user`) | `Apis[].AppIdUri` + `Scopes[]` | **Yes** — the emulator matches a requested scope to an API by AppIdUri prefix |
| Daemon `44444444…` | `…M2M:ClientId` | `Clients[].ClientId` | **Yes** — identifies the M2M caller |
| SPA `33333333…` | frontend `auth.clientId` | *(not needed)* | No — the emulator echoes it as the `id_token` audience; it doesn't validate it |
| Tenant id `11111111…` | backend `ValidIssuer` (inside the issuer string) | `TenantId` (+ `PublicBaseUrl`) | **Yes** — together they form the fixed issuer `{PublicBaseUrl}/{TenantId}/v2.0/`, which `ValidIssuer` must equal exactly |
| Policy `B2C_1A_SIGNIN` | the authority's policy path segment | the `{policy}` URL segment | any name works; a name containing `PASSWORDRESET` renders the reset screen |

### The short version

- **Reuse your real ids** — that's the point, so emulated tokens look exactly like production ones.
- **Mirror two of them into the emulator:** the API audience (`Apis[].Audience`) and any daemon
  client id (`Clients[].ClientId`). The SPA client id doesn't need to be in the emulator config.
- **Only the authority host changes** in your app config (`b2clogin.com` → the emulator), plus
  the two .NET knobs (`RequireHttpsMetadata: false` and an explicit `ValidIssuer`).
