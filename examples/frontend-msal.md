# Pointing a frontend (MSAL.js) at the emulator

Configure MSAL's authority and `knownAuthorities` to point at the emulator.

```jsonc
{
  "auth": {
    "clientId": "<your-spa-client-id>",
    "authority": "https://localhost:8080/yourtenant.onmicrosoft.com/B2C_1A_SIGNIN",
    "knownAuthorities": [ "localhost:8080" ],
    "redirectUri": "http://localhost:4200"
  }
}
```

## The https requirement

MSAL.js **rejects a non-https authority** (`authority_uri_insecure`) and does *not* make an
exception for `localhost`. So the browser must reach the emulator over **https**:

- Run the emulator with an https URL the browser trusts. Standalone it already serves
  `https://localhost:7299`; in a container, terminate TLS with a trusted dev cert (e.g. the
  .NET dev cert via `dotnet dev-certs https --trust`, or your own).
- Set `Emulator__PublicBaseUrl` to the **https** URL the browser uses, so the issuer matches.

## After changing the authority

MSAL caches state in `sessionStorage` tied to the previous authority — sign in from a fresh
incognito window after repointing it.
