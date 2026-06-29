# Pointing any OIDC stack at the emulator

The emulator serves a standard OpenID Connect discovery document, so most OIDC/JWT libraries
(Node `passport`/`jose`, Java/Spring Security, Go, Python, etc.) need only the authority/issuer.

## Discovery

```
GET {PublicBaseUrl}/{tenant}/{policy}/v2.0/.well-known/openid-configuration
```

e.g. `http://localhost:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN/v2.0/.well-known/openid-configuration`

From there a library discovers the `jwks_uri`, `authorization_endpoint` and `token_endpoint`.

## What your validator needs to accept

- **Issuer** — the fixed value `{PublicBaseUrl}/{TenantId}/v2.0/`. The discovery document's
  endpoints are returned relative to the host you call it on (so browser and in-cluster
  callers each get a reachable URL), but the `iss` on every token is always this fixed value.
  Configure your validator's expected issuer accordingly.
- **Signing** — RS256, key published at the `jwks_uri`.
- **Audience** — the matched API's `Audience` (see your `Apis[]` config).
- **http metadata** — if you point at the `http` endpoint locally, allow non-https metadata
  retrieval (most libraries have a flag for this; it is dev-only).

## Token shape

Tokens are Azure AD B2C-shaped: claims include `oid`, `sub`, `emails` (array), `tfp`, `scp`
(space-delimited), plus any per-user `Claims` you configured. That fidelity is the point —
your app validates emulator tokens exactly as it would real B2C ones.
