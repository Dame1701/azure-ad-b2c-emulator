# Integration guide

How to wire this emulator into a real application stack — backend APIs, a SPA frontend, and
machine-to-machine daemons — running locally (typically in Kubernetes/kind). It uses a
typical multi-tier platform (SPA + APIs + daemons) as the worked example, but everything here
is generic; substitute your own tenant, client ids and user store.

If you just want to run the emulator standalone, see the [README](../README.md). This guide
is about making your *own* application authenticate against it instead of Azure AD B2C.

---

## Contents

1. [The mental model](#1-the-mental-model)
2. [Identity: users, object ids and how logins resolve](#2-identity-users-object-ids-and-how-logins-resolve)
3. [Running the emulator inside your cluster](#3-running-the-emulator-inside-your-cluster)
4. [Adding an "emulated" environment to your apps](#4-adding-an-emulated-environment-to-your-apps)
5. [Pointing each tier at the emulator](#5-pointing-each-tier-at-the-emulator)
6. [Custom login / password-reset screens](#6-custom-login--password-reset-screens)
7. [End-to-end setup checklist](#7-end-to-end-setup-checklist)

---

## 1. The mental model

In production your platform authenticates against **Azure AD B2C** in three ways:

| Flow | Who | What |
| --- | --- | --- |
| **Interactive sign-in** | the SPA frontend (MSAL.js) | redirects the user to B2C, gets `id`/`access` tokens back |
| **Token validation** | the backend APIs | validate the JWT bearer tokens on each request |
| **Machine-to-machine** | daemons / service-to-service | `client_credentials` grant for an app-only token |

The emulator is a **drop-in for all three**. It is *development only* — it checks no
passwords (any password is accepted for a known email) and is not hardened.

Two things you must do to adopt it, and the rest of this guide expands on them:

- **Point each app at the emulator** instead of Azure (configuration, mostly).
- **Make the emulator issue identities your app recognises** (the `Users` you configure).

The emulator does **not** own your user database. It mints tokens; your application is still
responsible for turning the token's identity into a user/roles. So the identities the
emulator issues have to line up with whatever your app already uses to resolve a login.

---

## 2. Identity: users, object ids and how logins resolve

This is the part most people get wrong first, so it's worth being explicit.

When someone signs in, the emulator looks up the chosen email in its configured `Users` list
and mints a token carrying that user's **object id** as the standard Azure AD claims:

```jsonc
{
  "oid":   "<ObjectId>",          // Azure AD object id
  "sub":   "<ObjectId>",
  "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier": "<ObjectId>",
  "emails": [ "<Email>" ],
  "name":  "<DisplayName>",
  // ...plus everything in the user's Claims map (e.g. B2C extension attributes)
}
```

Your application then takes that token and resolves it to a real user. **Whatever mechanism
you use for that resolution determines what `ObjectId` you must put in the emulator config.**

> **Worked example.** Say your backend has a claims-transformation step that, on
> each request, looks the caller up in the database **by Azure AD object id** (an
> `AadObjectId` column) and enriches the principal with the user's roles and organisation. So
> the emulator's `Users[].ObjectId` values are set to the **same object ids as the seeded
> user rows in the local dev database**. Sign in as `alice@example.com` → token carries her
> `oid` → the transformation finds her DB row → she has her real roles. If the object id
> doesn't match a row, the user authenticates but is "not found" by the app (typically a 401
> or an empty profile).

So the rule is:

- **`Users[].ObjectId` must equal the identifier your platform keys users on** (object id in a
  users table, a mapping table, an external directory lookup, etc.).
- **`Users[].Email` / `DisplayName`** are what the token carries and what shows in the login
  dropdown.
- **`Users[].Claims`** reproduce any B2C **custom attributes** your app reads. For example, if
  your app grants platform-admin rights when a token carries `extension_IsAdmin == "true"`,
  add that to the relevant user:

  ```jsonc
  {
    "ObjectId": "11111111-1111-1111-1111-111111111111",
    "Email": "admin@example.com",
    "DisplayName": "Platform Admin",
    "Label": "admin",                       // optional badge in the login dropdown
    "Group": "Administrators",              // optional <optgroup> grouping
    "Claims": { "extension_IsAdmin": "true" }
  }
  ```

There is no password and no self-registration: the `Users` list is the complete set of people
who can sign in interactively. With an empty list, interactive login is disabled (the
emulator logs a warning saying so); machine-to-machine still works.

### Machine-to-machine identities

Daemon / service-to-service callers don't use `Users` at all. They use the `client_credentials`
grant, and you configure them under `Clients`:

```jsonc
"Clients": [
  { "ClientId": "<daemon-app-id>", "Secret": "", "Audience": "<api-app-id>",
    "Scopes": [ "system.access" ] }
]
```

The issued app-only token carries the `ClientId` as `sub`/`azp` and the configured `Scopes`
as the scope claim your backend checks to recognise the caller. A blank `Secret` accepts any
secret (handy for local dev; the daemon still sends one).

---

## 3. Running the emulator inside your cluster

For a Kubernetes/kind setup the emulator runs as a normal **Deployment + Service**, and the
single most important concept is that **the browser and your pods reach it on different
addresses** — but every token must carry the **same issuer**.

> **Run it as a single replica (`replicas: 1`).** The signing key and the authorization-code /
> refresh-token stores are per-pod and in-memory, so scaling the emulator beyond one replica
> breaks token validation and the code exchange intermittently. Many *application* pods sharing
> the one emulator is fine; the emulator itself must not be scaled. See the README's
> [Limitations](../README.md#limitations) for the full reasoning.

### The two reachability planes

| Caller | Reaches the emulator at | Why |
| --- | --- | --- |
| **Browser** (MSAL.js) | `https://localhost:8080` (host port → cluster) | MSAL.js rejects any non-`https` authority, with no `localhost` exemption |
| **Pods** (backend APIs) | `http://auth-emulator:8080` (in-cluster DNS) | pods can't use a `localhost` URL; they use the Service name |

The emulator squares this circle automatically:

- The **issuer is fixed** to `Emulator:PublicBaseUrl` (e.g. `https://localhost:8080`), so every
  token's `iss` is identical no matter who requested it.
- The **discovery document's endpoints are returned relative to the host the caller used**, so
  the browser gets `https://localhost:8080/...` and a pod gets `http://auth-emulator:8080/...`
  — each a URL it can actually reach — while both validate against the one fixed issuer.

You set `PublicBaseUrl` to **the URL the browser uses**, via an environment variable
(env wins over the mounted config file):

```yaml
env:
  - name: Emulator__PublicBaseUrl
    value: "https://localhost:8080"
```

### TLS for the browser

Because the browser needs `https`, you need TLS *somewhere* on the browser-facing path. Two
common approaches:

- **Terminate TLS at an ingress / gateway** in front of the emulator (most production-like).
- **Run Kestrel with a dev cert** the browser trusts (simplest for kind). Export the .NET dev
  cert into a secret and point Kestrel at it:

  ```yaml
  env:
    - name: ASPNETCORE_URLS
      value: "http://+:8080;https://+:8443"
    - name: ASPNETCORE_Kestrel__Certificates__Default__Path
      value: /tls/cert.pfx
    - name: ASPNETCORE_Kestrel__Certificates__Default__Password
      value: localdev
  volumeMounts:
    - { name: tls, mountPath: /tls, readOnly: true }
  ```

  Pods still use plain `http://auth-emulator:8080`; only the browser path needs https.

### Service mesh & network policy

If you run a mesh (e.g. Istio ambient) or NetworkPolicies, the emulator carries **plaintext
NodePort traffic from the browser**, which strict mTLS / default-deny policies will drop. Keep
the emulator pod **out of the mesh** and **exempt from the restrictive NetworkPolicy**. With
Istio ambient that means the pod label:

```yaml
metadata:
  labels:
    istio.io/dataplane-mode: none      # opt OUT of the ambient mesh
    # and omit whatever label your NetworkPolicy selects on
```

### Injecting your config and assets (don't bake them in)

The published image ships **generic sample config**. You inject your real tenant/clients/users
and branding at deploy time with a **ConfigMap mounted over the image's files** — so your
private config never lives in the public image:

```yaml
# configmap.yaml — your real config + branding, kept in YOUR (private) repo
apiVersion: v1
kind: ConfigMap
metadata: { name: auth-emulator-config }
data:
  appsettings.json: |-            # full replace of the baked sample (see note below)
    { "Emulator": { "Tenant": "...", "Apis": [...], "Clients": [...], "Users": [...],
                    "Branding": { "ProductName": "Acme", "LogoPath": "/assets/logo.png" } } }
  styles.css: |-
    /* your brand CSS */
binaryData:
  logo.png: <base64>              # PNG is binary → binaryData
```

```yaml
# deployment.yaml (excerpt)
volumeMounts:
  - { name: config, mountPath: /app/appsettings.json,             subPath: appsettings.json }
  - { name: config, mountPath: /app/wwwroot/assets/styles.css,    subPath: styles.css }
  - { name: config, mountPath: /app/wwwroot/assets/logo.png,      subPath: logo.png }
volumes:
  - name: config
    configMap: { name: auth-emulator-config }
```

> **Mount over `appsettings.json` vs `appsettings.Local.json`.** Mounting a complete
> `appsettings.json` (full replace) is the most robust option when you own the whole config,
> because .NET config **merges JSON arrays by index, not by appending** — a partial
> `appsettings.Local.json` override with a shorter `Users` array can leave stale sample
> entries at higher indices. Provide complete arrays, or replace the whole file.

### Getting the image onto the cluster

The cluster uses the image with `imagePullPolicy: IfNotPresent`, and your local-dev script
pulls it on the host and loads it onto the node, so the cluster itself needs no registry
access:

```bash
docker pull ghcr.io/OWNER/azure-ad-b2c-emulator:<tag>
kind load docker-image ghcr.io/OWNER/azure-ad-b2c-emulator:<tag> -n <cluster>
helm upgrade auth-emulator ./charts/auth-emulator --install
```

A **dev escape hatch**: build this repo locally and tag the result with the same image
reference; the load step then uses your local build instead of the published image — handy
when you're changing the emulator itself.

---

## 4. Adding an "emulated" environment to your apps

Don't contaminate your real configuration. Add a **dedicated ASP.NET environment** — e.g.
`DevelopmentEmulator` — that only your local stack uses, so production config is untouched and
all the emulator-specific settings are config-gated.

- Each service gets an overlay file, e.g. `appsettings.DevelopmentEmulator.json`, with the
  auth pointed at the emulator (next section).
- Set the environment for the local deployment, e.g. in your Helm values:

  ```yaml
  aspNetCore:
    environment: DevelopmentEmulator
  ```

  or as an env var on the pod:

  ```yaml
  env:
    - { name: ASPNETCORE_ENVIRONMENT, value: DevelopmentEmulator }
  ```

Production runs as `Production`/`Staging`/etc. and never sets these keys, so nothing about the
emulator can leak into a real deployment.

---

## 5. Pointing each tier at the emulator

> For a **side-by-side before/after** of real-Azure vs emulator config across a backend, a
> daemon and a frontend — plus a table of exactly which client id / audience maps to which
> emulator setting — see [examples/config-mapping.md](../examples/config-mapping.md).

### Backend API (.NET / Microsoft.Identity.Web)

In your `appsettings.DevelopmentEmulator.json`, point the authority at the **in-cluster** URL:

```jsonc
{
  "AzureAdB2C": {
    "Instance": "http://auth-emulator:8080/",
    "Domain": "yourtenant.onmicrosoft.com",
    "ClientId": "<your-api-app-id>",        // must equal the emulator's Apis[].Audience
    "SignUpSignInPolicyId": "B2C_1A_SIGNIN"
  },
  "AzureAdB2C:ValidIssuer": "https://localhost:8080/<tenant-id>/v2.0/"
}
```

Then the **two knobs** Microsoft.Identity.Web doesn't expose through config — set them in
startup, guarded to the emulated environment:

```csharp
if (builder.Environment.EnvironmentName == "DevelopmentEmulator")
{
    builder.Services.Configure<JwtBearerOptions>(
        JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.RequireHttpsMetadata = false;   // the in-cluster authority is http
            o.TokenValidationParameters.ValidIssuer =
                "https://localhost:8080/<tenant-id>/v2.0/";   // the fixed issuer
        });
}
```

> **Why `ValidIssuer` is mandatory.** Microsoft.Identity.Web wires up a B2C issuer validator
> that only accepts Azure-cloud issuer templates and rejects the emulator's custom issuer with
> **`IDX40001: Issuer ... does not match any of the valid issuers`** (it can surface
> downstream as a misleading `invalid_token ... issuer '(null)'`). Setting `ValidIssuer`
> explicitly makes the validator accept it. Note the authority is the **http in-cluster** URL,
> but the issuer it validates is the **fixed `PublicBaseUrl`** value — they differ on purpose.

See [examples/microsoft-identity-web.md](../examples/microsoft-identity-web.md) for the
copy-paste version.

### Frontend SPA (MSAL.js)

Point MSAL at the **browser** URL (https):

```jsonc
{
  "auth": {
    "clientId": "<your-spa-app-id>",
    "authority": "https://localhost:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN",
    "knownAuthorities": [ "localhost:8080" ],
    "redirectUri": "http://localhost:4200"
  }
}
```

After changing the authority, sign in from a **fresh incognito window** — MSAL caches state
in `sessionStorage` tied to the previous authority. See
[examples/frontend-msal.md](../examples/frontend-msal.md).

### Machine-to-machine daemons

Point the daemon's token authority at the emulator and use a `ClientId` that matches one of
the emulator's `Clients[]`. Any secret is accepted unless you configure one.

### Non-.NET backends

Any OIDC/JWT library only needs the discovery URL and the expected issuer — see
[examples/generic-oidc.md](../examples/generic-oidc.md).

---

## 6. Custom login / password-reset screens

The sign-in and password-reset pages are made of **files you can replace by mounting**, so you
can match your brand without rebuilding the image. There are three layers:

| Layer | File | Customise by |
| --- | --- | --- |
| Page chrome (HTML) | `wwwroot/templates/login-layout.html` | mount your own template |
| Styling | `wwwroot/assets/styles.css` | mount your own stylesheet |
| Logo / wordmark | `Branding.LogoPath` + `wwwroot/assets/logo.png` | set config + mount the image |

### Branding via config

```jsonc
"Branding": {
  "ProductName": "Acme",                  // page title + wordmark when no logo
  "EmulatorTag": "Azure AD B2C Emulator", // small label beside the brand
  "LogoPath": "/assets/logo.png"          // omit/empty to show ProductName as text
}
```

### The login template

`login-layout.html` is loaded at runtime and has these placeholders substituted:

| Placeholder | Filled with |
| --- | --- |
| `{{Title}}` | `"<ProductName> - Sign-in"` |
| `{{StylesHref}}` | `/assets/styles.css` |
| `{{Brand}}` | the logo element, or the product-name wordmark |
| `{{Tag}}` | `Branding.EmulatorTag` |
| `{{Body}}` | the rendered card contents (form fields + developer quick-pick) |

You control the entire page **chrome** by replacing the template; the **form fields**
themselves (`{{Body}}`) are rendered by the app. To restyle deeply, ship your own
`styles.css`. Mount your versions:

```yaml
volumeMounts:
  - { name: config, mountPath: /app/wwwroot/templates/login-layout.html, subPath: login-layout.html }
  - { name: config, mountPath: /app/wwwroot/assets/styles.css,           subPath: styles.css }
```

### The password-reset screen

There's no separate page to configure: the emulator renders the **reset variant of the same
template** (email field only, titled "Reset Password") whenever the **policy name contains
`PASSWORDRESET`** — e.g. an authorize request under `.../B2C_1A_PASSWORDRESET/...`. So if your
frontend triggers a password-reset policy, it just works, styled by the same template/CSS. As
with sign-in, no password is checked.

---

## 7. End-to-end setup checklist

A first-time bring-up for a typical platform:

1. **Decide the identities.** For each developer/test login, note the object id your platform
   resolves users by, and seed matching rows in your local DB.
2. **Write your config** (`appsettings.json` for the emulator) with your `Tenant`, `TenantId`,
   `Apis` (audience = your API app id), `Clients` (daemons), and `Users` (object ids + emails +
   any custom `Claims`), plus `Branding`.
3. **Create the ConfigMap** from that config (+ your `styles.css`, `logo.png`, optional
   `login-layout.html`) in your private repo's deployment chart.
4. **Deploy the emulator** as a Deployment + Service: pull + `kind load` the published image,
   set `Emulator__PublicBaseUrl` to the browser https URL, mount the ConfigMap over
   `/app/appsettings.json` and `wwwroot/assets/*`, sort out browser TLS, and keep the pod out
   of any strict mesh/NetworkPolicy.
5. **Add the `DevelopmentEmulator` environment** to your services and the per-service
   `appsettings.DevelopmentEmulator.json` overlays (authority → `http://auth-emulator:8080`,
   `RequireHttpsMetadata: false`, `ValidIssuer` → the fixed issuer); add the
   `Configure<JwtBearerOptions>` snippet.
6. **Point the frontend** MSAL config at `https://localhost:8080`.
7. **Bring it all up**, then sign in: pick a seeded user, confirm the backend resolves them to
   the right roles, and confirm a daemon `client_credentials` call gets a token.

If sign-in works but the user 401s / isn't found, the `ObjectId` doesn't match a row in your
store (step 1). If you get `IDX40001`, the backend isn't getting `ValidIssuer` (step 5). Both
are covered in the [README troubleshooting table](../README.md#troubleshooting).
