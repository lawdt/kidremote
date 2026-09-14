using System.Security.Cryptography;
using System.Text;

namespace KidRemote.Core;

/// <summary>
/// Обёртка над DPAPI: данные шифруются ключом текущей учётной записи Windows.
/// Защищает от просмотра файлов "глазами", но не от целенаправленного извлечения
/// под той же учёткой — это обфускация, а не криптостойкая защита.
/// </summary>
internal static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KidRemote/v1/state");

    public static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static bool TryUnprotect(string payload, out string plain)
    {
        plain = string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(payload);
            var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            plain = Encoding.UTF8.GetString(decrypted);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
