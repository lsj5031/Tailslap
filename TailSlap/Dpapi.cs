using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public static class Dpapi
{
    private static int _protectFailureNotified;
    private static int _unprotectFailureNotified;

    public static string Protect(string plaintext) => Protect(plaintext, notifyOnFailure: true);

    public static string Protect(string plaintext, bool notifyOnFailure)
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
                Logger.Error($"DPAPI Protect failed: {ex.GetType().Name}");
            }
            catch { }
            if (notifyOnFailure && Interlocked.Exchange(ref _protectFailureNotified, 1) == 0)
            {
                try
                {
                    NotificationService.ShowError(
                        "TailSlap could not securely save sensitive data. Check your Windows account security settings and try again."
                    );
                }
                catch { }
            }
            // Fail gracefully: caller will treat empty result as "no key"
            return string.Empty;
        }
    }

    public static string Unprotect(string base64) => Unprotect(base64, notifyOnFailure: true);

    public static string Unprotect(string base64, bool notifyOnFailure)
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
                Logger.Error($"DPAPI Unprotect failed: {ex.GetType().Name}");
            }
            catch { }
            if (notifyOnFailure && Interlocked.Exchange(ref _unprotectFailureNotified, 1) == 0)
            {
                try
                {
                    NotificationService.ShowError(
                        "TailSlap could not decrypt saved sensitive data. Re-enter the affected API key and try again."
                    );
                }
                catch { }
            }
            // Fail gracefully: caller will see an empty API key and simply not send auth
            return string.Empty;
        }
    }
}
