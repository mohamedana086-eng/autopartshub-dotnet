using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoPartsHub.Api.Auth;

/// <summary>
/// The signed session cookie: <c>base64url(payload) . base64url(HMAC-SHA256)</c>.
/// </summary>
/// <remarks>
/// Interoperable with the Node implementation on purpose. Both APIs read the
/// same database and, during the port, may both be reachable — a customer
/// signed in against one must not be signed out by the other. That means the
/// same cookie name, the same token layout, the same signing input, and
/// <c>exp</c> in milliseconds because <c>Date.now()</c> produces milliseconds.
///
/// A cookie is verified by re-signing the body exactly as it arrived, so the
/// key order inside the JSON never has to match — only the bytes that were
/// signed.
/// </remarks>
public sealed class SessionTokens(string secret)
{
    public const string CookieName = "aph_session";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Encode(SessionPayload payload)
    {
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson));
        return $"{body}.{Sign(body)}";
    }

    /// <summary>The payload, or null if the token is absent, forged or expired.</summary>
    public SessionPayload? Decode(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var dot = token.IndexOf('.');
        if (dot < 1 || dot == token.Length - 1) return null;

        var body = token[..dot];
        var signature = token[(dot + 1)..];

        // Fixed-time comparison, unlike the Node original, which compares two
        // strings with !== and so leaks how long a forged signature matched
        // for. Nothing else about the check changes.
        var expected = Encoding.ASCII.GetBytes(Sign(body));
        var actual = Encoding.ASCII.GetBytes(signature);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        try
        {
            var payload = JsonSerializer.Deserialize<SessionPayload>(FromBase64Url(body), PayloadJson);
            if (payload is null) return null;

            // Milliseconds, matching Date.now() on the other side.
            return payload.Exp < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string Sign(string data)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data));
        return Base64Url(mac);
    }

    /// <summary>base64url: no padding, '-' for '+', '_' for '/'.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}

/// <param name="Exp">Milliseconds since the epoch, as Date.now() gives.</param>
public record SessionPayload(
    string UserId,
    string Role,
    string? CategoryId,
    string Name,
    long Exp);
