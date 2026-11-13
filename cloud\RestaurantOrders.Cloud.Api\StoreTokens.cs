using System.Security.Cryptography;
using System.Text;

namespace RestaurantOrders.Cloud.Api;

public static class StoreTokens
{
    public static string Create()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
