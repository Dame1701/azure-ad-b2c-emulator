# Contributing

Thanks for your interest in improving the Azure AD B2C emulator.

## Development

```bash
dotnet run --project src/AzureAdB2cEmulator
```

The emulator listens on `https://localhost:7299` and `http://localhost:5299`. Run
`dotnet dev-certs https --trust` once so the browser trusts the TLS cert.

## Guidelines

- Keep the project dependency-light and self-contained — it must build and run in isolation.
- This is a **development-only** tool. Do not add anything that implies it is safe for
  production (it checks no passwords and is not hardened).
- Anything specific to one organisation belongs in *configuration*, not in code. New
  behaviour should be driven by `appsettings` so every adopter can use it.
- Match the existing style. Match the B2C token/claim shape — fidelity to real Azure AD B2C
  is the point.

## Pull requests

Open an issue first for anything non-trivial so we can agree the approach. Keep PRs focused.
