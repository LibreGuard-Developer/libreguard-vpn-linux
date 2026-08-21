using System.Security.Cryptography;
using System.Text;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class DeviceIdentityService(ISecretStore secretStore, string appVersion) : IDeviceIdentityService
{
    private const string DeviceIdKey = "device-id";
    private const string PrivateKeyKey = "device-private-key";
    private const string Algorithm = "RSA-OAEP-256";

    public async Task<DeviceRegistrationPayload> GetRegistrationPayloadAsync(CancellationToken cancellationToken)
    {
        var identity = await EnsureIdentityAsync(cancellationToken);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.PrivateKey), out _);

        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var keyId = ComputeKeyId(publicKey);

        return new DeviceRegistrationPayload(identity.DeviceId, appVersion, publicKey, keyId, Algorithm);
    }

    public async Task<string> DecryptPassphraseAsync(EncryptedPassphrase encryptedPassphrase, CancellationToken cancellationToken)
    {
        if (!string.Equals(encryptedPassphrase.Algorithm, Algorithm, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported encrypted passphrase algorithm: {encryptedPassphrase.Algorithm}");
        }

        var identity = await EnsureIdentityAsync(cancellationToken);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.PrivateKey), out _);
        var cipherBytes = Convert.FromBase64String(encryptedPassphrase.Ciphertext);
        var plainBytes = rsa.Decrypt(cipherBytes, RSAEncryptionPadding.OaepSHA256);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private async Task<DeviceIdentity> EnsureIdentityAsync(CancellationToken cancellationToken)
    {
        var deviceId = await secretStore.GetAsync(DeviceIdKey, cancellationToken);
        var privateKey = await secretStore.GetAsync(PrivateKeyKey, cancellationToken);

        if (!string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(privateKey))
        {
            return new DeviceIdentity(deviceId, privateKey);
        }

        deviceId = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N") : deviceId;

        using var rsa = RSA.Create(2048);
        privateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());

        await secretStore.SetAsync(DeviceIdKey, deviceId, cancellationToken);
        await secretStore.SetAsync(PrivateKeyKey, privateKey, cancellationToken);
        return new DeviceIdentity(deviceId, privateKey);
    }

    private static string ComputeKeyId(string publicKey)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(publicKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record DeviceIdentity(string DeviceId, string PrivateKey);
}
