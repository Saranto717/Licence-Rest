using System.Security.Cryptography;
using System.Text.Json;

namespace RestaurantOrders.Cloud.Api;

public sealed class LicenseSigner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string privateKeyPem;

    public LicenseSigner(string privateKeyPem)
    {
        this.privateKeyPem = privateKeyPem;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        if (rsa.KeySize < 2048)
            throw new InvalidOperationException("The remote license signing key must be at least 2048 bits.");
    }

    public SignedLicenseContract Sign(CloudLicensePayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new SignedLicenseContract
        {
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(signature)
        };
    }
}
