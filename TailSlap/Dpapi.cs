using System;
using System.Security.Cryptography;
using System.Text;

public static class Dpapi
{
    public static string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string Unprotect(string base64)
    {
        var enc = Convert.FromBase64String(base64);
        var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(dec);
    }
}
