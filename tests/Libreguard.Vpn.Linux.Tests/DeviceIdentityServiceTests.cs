using System.Security.Cryptography;
using System.Text;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class DeviceIdentityServiceTests
{
    [Fact]
    public async Task DeviceIdentity_IsStable_AndDecryptsRsaOaep256()
    {
        var store = new InMemorySecretStore();
        var service = new DeviceIdentityService(store, "Linux/1.1.17");

        var first = await service.GetRegistrationPayloadAsync(CancellationToken.None);
        var second = await service.GetRegistrationPayloadAsync(CancellationToken.None);

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal("Linux/1.1.17", first.AppVersion);
        Assert.Equal("RSA-OAEP-256", first.PublicKeyAlgorithm);
        Assert.Equal(first.PublicKeyId, second.PublicKeyId);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(first.PublicKey), out _);
        var cipher = rsa.Encrypt(Encoding.UTF8.GetBytes("vpn-passphrase"), RSAEncryptionPadding.OaepSHA256);

        var decrypted = await service.DecryptPassphraseAsync(
            new EncryptedPassphrase("RSA-OAEP-256", first.PublicKeyId, Convert.ToBase64String(cipher)),
            CancellationToken.None);

        Assert.Equal("vpn-passphrase", decrypted);
    }
}
