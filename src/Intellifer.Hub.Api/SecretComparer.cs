using System.Security.Cryptography;
using System.Text;

namespace Intellifer.Hub.Api;

public static class SecretComparer
{
    public static bool Equals(string provided, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
