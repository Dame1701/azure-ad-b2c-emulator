// SPDX-License-Identifier: MIT
// Part of azure-ad-b2c-emulator: a local stand-in for Azure AD B2C.

using System.Security.Cryptography;
using System.Text.Json;
using AzureAdB2cEmulator.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace AzureAdB2cEmulator.Services;

/// <summary>
///     Owns the RSA key used to sign tokens. The key is generated on first run and
///     persisted to disk so that the public JWKS (and therefore previously issued
///     tokens) survive restarts.
/// </summary>
public sealed class SigningKeyProvider : IDisposable
{
    private readonly RSA _rsa;

    public SigningKeyProvider(EmulatorOptions options, IHostEnvironment environment, ILogger<SigningKeyProvider> logger)
    {
        var path = Path.IsPathRooted(options.SigningKeyPath)
            ? options.SigningKeyPath
            : Path.Combine(environment.ContentRootPath, options.SigningKeyPath);

        _rsa = RSA.Create(2048);

        if (File.Exists(path))
        {
            var persisted = JsonSerializer.Deserialize<PersistedKey>(File.ReadAllText(path))
                            ?? throw new InvalidOperationException($"Signing key at '{path}' could not be read.");
            _rsa.ImportParameters(persisted.ToParameters());
            KeyId = persisted.KeyId;
            logger.LogInformation("Loaded signing key {KeyId} from {Path}", KeyId, path);
        }
        else
        {
            KeyId = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(path, JsonSerializer.Serialize(PersistedKey.From(KeyId, _rsa.ExportParameters(true))));
            logger.LogInformation("Generated new signing key {KeyId} and saved to {Path}", KeyId, path);
        }

        var securityKey = new RsaSecurityKey(_rsa)
        {
            KeyId = KeyId
        };
        SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
    }

    public string KeyId { get; }

    public SigningCredentials SigningCredentials { get; }

    public void Dispose() => _rsa.Dispose();

    /// <summary>Builds the public JWK document served from the JWKS endpoint.</summary>
    public object GetPublicJsonWebKey()
    {
        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        return new
        {
            kty = "RSA",
            use = "sig",
            kid = KeyId,
            alg = "RS256",
            n = Base64UrlEncoder.Encode(parameters.Modulus),
            e = Base64UrlEncoder.Encode(parameters.Exponent)
        };
    }

    private sealed record PersistedKey(
        string KeyId,
        string Modulus,
        string Exponent,
        string D,
        string P,
        string Q,
        string DP,
        string DQ,
        string InverseQ)
    {
        public static PersistedKey From(string keyId, RSAParameters p) => new(
            keyId,
            Base64UrlEncoder.Encode(p.Modulus),
            Base64UrlEncoder.Encode(p.Exponent),
            Base64UrlEncoder.Encode(p.D),
            Base64UrlEncoder.Encode(p.P),
            Base64UrlEncoder.Encode(p.Q),
            Base64UrlEncoder.Encode(p.DP),
            Base64UrlEncoder.Encode(p.DQ),
            Base64UrlEncoder.Encode(p.InverseQ));

        public RSAParameters ToParameters() => new()
        {
            Modulus = Base64UrlEncoder.DecodeBytes(Modulus),
            Exponent = Base64UrlEncoder.DecodeBytes(Exponent),
            D = Base64UrlEncoder.DecodeBytes(D),
            P = Base64UrlEncoder.DecodeBytes(P),
            Q = Base64UrlEncoder.DecodeBytes(Q),
            DP = Base64UrlEncoder.DecodeBytes(DP),
            DQ = Base64UrlEncoder.DecodeBytes(DQ),
            InverseQ = Base64UrlEncoder.DecodeBytes(InverseQ)
        };
    }
}