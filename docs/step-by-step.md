# Step-by-step setup (Kubernetes / kind)

A do-this-in-order walkthrough for switching a local app stack — a SPA, one or more backend
APIs, and optionally machine-to-machine daemons — from real Azure AD B2C to the emulator, the
way a typical Kubernetes/kind setup does it.

This is the **tutorial**; for the *why* behind each step see the
[integration guide](integration-guide.md), and for the exact config swaps see
[config-mapping.md](../examples/config-mapping.md). Example ids below are consistent with that
mapping doc (API `2222…`, SPA `3333…`, daemon `4444…`, tenant `1111…`).

> Just want to kick the tyres with no app? `docker run` the image — see the
> [README quick start](../README.md#quick-start). This guide is for wiring it into *your* stack.

---

## Step 0 — Prerequisites

- Docker, `kubectl`, `kind`, `helm`
- Your app's .NET SDK if your backend is .NET (for the dev cert + builds)
- A running kind cluster for your stack

**Checkpoint:** `kubectl get nodes` shows your cluster ready.

---

## Step 1 — Map out your identities

The emulator issues tokens; **your app still resolves them to real users**, so the object ids
it issues must line up with your user store.

1. List the developers/test accounts you want to sign in as.
2. For each, find the **object id your platform keys users on** (e.g. the `AadObjectId` column,
   or whatever your claims-transformation / user lookup uses).
3. Make sure your **local dev database has matching rows** for those object ids.
4. Note any **custom B2C attributes** your app reads (e.g. `extension_IsAdmin`).

**Checkpoint:** you have, per user: object id, email, display name, and any custom claims.

---

## Step 2 — Gather your application ids

From your existing Azure AD B2C setup (or `config-mapping.md` if starting fresh):

| Need | Example |
| --- | --- |
| Tenant domain + tenant id | `yourtenant.onmicrosoft.com` / `1111…` |
| API app registration client id (the token **audience**) | `2222…` |
| API App ID URI + delegated scope | `https://yourtenant.onmicrosoft.com/api` + `access_as_user` |
| Daemon (M2M) client id(s), if any | `4444…` |
| Sign-in policy name | `B2C_1A_SIGNIN` |

**Checkpoint:** you can fill in every row above.

---

## Step 3 — Write the emulator config

Create `appsettings.json` for the emulator from Steps 1–2:

```jsonc
{
  "Emulator": {
    "Tenant":   "yourtenant.onmicrosoft.com",
    "TenantId": "11111111-1111-1111-1111-111111111111",
    "Branding": { "ProductName": "Acme", "EmulatorTag": "Azure AD B2C Emulator",
                  "LogoPath": "/assets/logo.png" },
    "Apis": [
      { "Audience": "22222222-2222-2222-2222-222222222222",
        "AppIdUri": "https://yourtenant.onmicrosoft.com/api",
        "Scopes":   [ "access_as_user" ] }
    ],
    "Clients": [
      { "ClientId": "44444444-4444-4444-4444-444444444444", "Secret": "",
        "Audience": "22222222-2222-2222-2222-222222222222", "Scopes": [ "system.access" ] }
    ],
    "Users": [
      { "ObjectId": "<object-id-from-step-1>", "Email": "dev@example.com",
        "DisplayName": "Dev User", "Claims": { "extension_IsAdmin": "true" } }
    ]
  }
}
```

Keep this in **your own (private) repo** — it holds your real ids.

**Checkpoint:** `Apis[].Audience` = your API client id; `Clients[].ClientId` = your daemon
client id; every `Users[].ObjectId` matches a row in your dev DB.

---

## Step 4 — Package config + branding as a ConfigMap

Put your `appsettings.json` (and optional `styles.css` / `logo.png`) into a ConfigMap so the
generic image stays config-free:

```bash
kubectl create configmap auth-emulator-config \
  --from-file=appsettings.json=./appsettings.json \
  --from-file=styles.css=./styles.css \
  --from-file=logo.png=./logo.png \
  --dry-run=client -o yaml | kubectl apply -f -
```

(Or template it in your Helm chart — see the [integration guide](integration-guide.md#injecting-your-config-and-assets-dont-bake-them-in).)

**Checkpoint:** `kubectl get configmap auth-emulator-config` exists.

---

## Step 5 — Prepare browser TLS

MSAL.js needs an **https** authority. The simplest local approach is the .NET dev cert:

```bash
dotnet dev-certs https --trust                      # trust it in your browser/OS (one-time)
dotnet dev-certs https --export-path ./cert.pfx --password localdev
kubectl create secret generic auth-emulator-tls \
  --from-file=cert.pfx=./cert.pfx --dry-run=client -o yaml | kubectl apply -f -
rm -f ./cert.pfx
```

**Checkpoint:** secret `auth-emulator-tls` exists; the dev cert is trusted.

---

## Step 6 — Deploy the emulator

Get the image onto the node, then apply a Deployment + Service. Pull + `kind load` so the
cluster needs no registry access:

```bash
docker pull ghcr.io/Dame1701/azure-ad-b2c-emulator:latest
kind load docker-image ghcr.io/Dame1701/azure-ad-b2c-emulator:latest -n <cluster>
```

A minimal `Deployment` + `Service` (adapt to your chart). Key points: **`replicas: 1`**,
`PublicBaseUrl` env = the **browser** https URL, the ConfigMap mounted over the image's files,
TLS cert mounted, and the pod kept out of any strict service mesh.

```yaml
apiVersion: apps/v1
kind: Deployment
metadata: { name: auth-emulator }
spec:
  replicas: 1                                   # MUST stay 1 (see README Limitations)
  selector: { matchLabels: { app: auth-emulator } }
  template:
    metadata:
      labels:
        app: auth-emulator
        istio.io/dataplane-mode: none           # if you run Istio ambient; keep it out of the mesh
    spec:
      containers:
        - name: auth-emulator
          image: ghcr.io/Dame1701/azure-ad-b2c-emulator:latest
          imagePullPolicy: IfNotPresent
          env:
            - { name: ASPNETCORE_URLS, value: "http://+:8080;https://+:8443" }
            - { name: ASPNETCORE_Kestrel__Certificates__Default__Path, value: /tls/cert.pfx }
            - { name: ASPNETCORE_Kestrel__Certificates__Default__Password, value: localdev }
            - { name: Emulator__PublicBaseUrl, value: "https://localhost:8080" }  # the BROWSER URL
          ports: [ { containerPort: 8080 }, { containerPort: 8443 } ]
          readinessProbe: { httpGet: { path: /health, port: 8080 } }
          volumeMounts:
            - { name: tls,    mountPath: /tls, readOnly: true }
            - { name: config, mountPath: /app/appsettings.json,          subPath: appsettings.json }
            - { name: config, mountPath: /app/wwwroot/assets/styles.css, subPath: styles.css }
            - { name: config, mountPath: /app/wwwroot/assets/logo.png,   subPath: logo.png }
      volumes:
        - { name: tls,    secret:    { secretName: auth-emulator-tls } }
        - { name: config, configMap: { name: auth-emulator-config } }
---
apiVersion: v1
kind: Service
metadata: { name: auth-emulator }
spec:
  selector: { app: auth-emulator }
  ports:
    - { name: http,  port: 8080, targetPort: 8080 }   # pods reach http://auth-emulator:8080
    - { name: https, port: 8443, targetPort: 8443 }
```

Expose the https port to the browser however your cluster does it (kind host-port mapping →
NodePort, or an ingress). Then verify:

```bash
kubectl rollout status deploy/auth-emulator
curl -sk https://localhost:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN/v2.0/.well-known/openid-configuration
```

**Checkpoint:** the discovery JSON comes back, and its `issuer` is your `PublicBaseUrl` value.
The pod log shows `Emulator ready: … N interactive user(s) …` (and *no* "0 users" warning).

---

## Step 7 — Point your backend services at it

Add a dedicated environment so production config is untouched. For each service:

1. Add `appsettings.DevelopmentEmulator.json`:
   ```jsonc
   {
     "AzureAdB2C": {
       "Instance": "http://auth-emulator:8080/",          // in-cluster authority (http)
       "Domain": "yourtenant.onmicrosoft.com",
       "ClientId": "22222222-2222-2222-2222-222222222222", // unchanged (= Apis[].Audience)
       "SignUpSignInPolicyId": "B2C_1A_SIGNIN",
       "ValidIssuer": "https://localhost:8080/11111111-1111-1111-1111-111111111111/v2.0/",
       "RequireHttpsMetadata": false
     }
   }
   ```
2. Apply the `JwtBearerOptions` snippet (Microsoft.Identity.Web needs it for `ValidIssuer` +
   `RequireHttpsMetadata`) — see [microsoft-identity-web.md](../examples/microsoft-identity-web.md).
3. Set `ASPNETCORE_ENVIRONMENT=DevelopmentEmulator` for the local deployment (Helm value or pod env).

**Checkpoint:** services start on the `DevelopmentEmulator` environment with no auth errors in
the logs.

---

## Step 8 — Point your frontend at it

In your local MSAL config:

```jsonc
"auth": {
  "clientId":         "33333333-3333-3333-3333-333333333333",
  "authority":        "https://localhost:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN",
  "knownAuthorities": [ "localhost:8080" ]
}
```

**Checkpoint:** the frontend builds/serves with the new authority.

---

## Step 9 — Point your daemons at it (if any)

Set each daemon's token authority to `http://auth-emulator:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN`,
keep its `ClientId` = `Clients[].ClientId`, and any secret (a blank `Secret` accepts anything).

**Checkpoint:** a daemon `client_credentials` call returns a token with the configured scope.

---

## Step 10 — Bring it up and verify end-to-end

1. Start your stack (services on `DevelopmentEmulator`, frontend pointed at the emulator).
2. Open the app in a **fresh incognito window** (MSAL caches state per authority).
3. Sign in — pick a seeded user from the emulator's dropdown (any password).
4. Confirm the app resolves you to the right user/roles.
5. (Optional) decode the access token and check `oid`, `aud`, `scp`, and any custom claim.

**Done.** You're authenticating entirely locally.

---

## If something's off

| Symptom | Likely step | Fix |
| --- | --- | --- |
| `authority_uri_insecure` (MSAL) | 5 / 8 | the authority must be `https` and trusted — check the dev cert + the frontend authority |
| `IDX40001: Issuer … does not match` | 7 | the backend isn't getting `ValidIssuer` — add it + the `JwtBearerOptions` snippet, rebuild, restart |
| Login works but user 401s / "not found" | 1 / 3 | `Users[].ObjectId` doesn't match a row in your store |
| `invalid_client` at the token endpoint | 3 / 9 | the caller's `ClientId` isn't in `Clients[]` |
| Login page shows no user dropdown; "0 users" in logs | 3 / 4 | the mounted config has an empty `Users` array |
| Intermittent token-validation / "unknown code" failures | 6 | you scaled the emulator beyond `replicas: 1` |

Full reference: the [integration guide](integration-guide.md) and the
[README troubleshooting table](../README.md#troubleshooting).
