using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TimeShift.Web;

/// <summary>
/// Сессия без хранилища: <c>&lt;expiry&gt;.&lt;подпись&gt;</c>, где expiry — Unix-секунды, подпись — HMAC-SHA256 от expiry
/// с ключом SHA256 от ключа доступа. Переживает перезапуск контейнера; смена ключа доступа разлогинивает все устройства.
/// </summary>
public static class SessionTokens
{
    public const string CookieName = "ts_session";

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(400);

    public static string Create(string accessKey, DateTimeOffset expiresAt)
    {
        var expiry = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{expiry}.{Base64Url.EncodeToString(Sign(accessKey, expiry))}";
    }

    public static bool IsValid(string accessKey, string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.IndexOf('.') is not (var dot and > 0))
        {
            return false;
        }

        var expiry = token[..dot];
        if (!long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            return false;
        }

        byte[] given;
        try
        {
            given = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(given, Sign(accessKey, expiry)) && unix > now.ToUnixTimeSeconds();
    }

    /// <summary>Ключ введён верно? Сравнение за постоянное время.</summary>
    public static bool KeyMatches(string accessKey, string? provided) =>
        provided is not null
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(accessKey)));

    private static byte[] Sign(string accessKey, string expiry)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("timeshift-session-v1|" + accessKey));
        return HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(expiry));
    }
}
