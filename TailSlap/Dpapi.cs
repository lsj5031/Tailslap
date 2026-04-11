using System;
using System.Security.Cryptography;
using System.Text;

public static class Dpapi
{
    public static string Protect(string plaintext)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(plaintext);
            var enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"DPAPI Protect failed: {ex.GetType().Name}");
            }
            catch { }
            // Fail gracefully: caller will treat empty result as "no key"
            return string.Empty;
        }
    }

    public static string Unprotect(string base64)
    {
        try
        {
            var enc = Convert.FromBase64String(base64);
            var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"DPAPI Unprotect failed: {ex.GetType().Name}");
            }
            catch { }
            // Fail gracefully: caller will see an empty API key and simply not send auth
            return string.Empty;
        }
    }
}
