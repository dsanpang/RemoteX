using System;
using System.Security.Cryptography;
using System.Text;

namespace RemoteX;

internal static class CredentialProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyRdpManager|Credential|v1");

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        if (IsProtected(plainText)) return plainText;

        try
        {
            var input = Encoding.UTF8.GetBytes(plainText);
            var cipher = ProtectedData.Protect(input, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        catch
        {
            AppLogger.Warn("password protect failed, returned empty value");
            // 严格模弝：禝止回蝽为明文入库
            return string.Empty;
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!IsProtected(stored))
        {
            AppLogger.Warn("detected unexpected non-encrypted password payload");
            return string.Empty;
        }

        try
        {
            var payload = stored[Prefix.Length..];
            var cipher = Convert.FromBase64String(payload);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            AppLogger.Warn("password unprotect failed, returned empty value");
            // 解密失败返回空串，靿兝把密文当密砝继续使�?
            return string.Empty;
        }
    }

    public static bool IsProtected(string value)
        => value.StartsWith(Prefix, StringComparison.Ordinal);
}
