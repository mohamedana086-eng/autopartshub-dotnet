using System.Security.Cryptography;
using System.Text;

namespace AutoPartsHub.Api.Auth;

/// <summary>
/// Cross-site request forgery.
/// </summary>
/// <remarks>
/// The storefront is Angular, and Angular's HttpClient already does its half:
/// on every request that is not GET or HEAD, to its own origin, it looks for a
/// cookie and copies it into a header. What it will not do is invent one — the
/// interceptor sends the header only when the cookie is already there. So the
/// whole feature is the other half: put the cookie out, and refuse a mutating
/// request whose header does not match it.
///
/// The names are Angular's defaults. Changing either without changing the
/// storefront's <c>withXsrfConfiguration</c> breaks every write at once.
///
/// Why the header is most of the protection: a page on another origin can make
/// the browser POST here with the victim's cookies attached, but it cannot set
/// a custom header on that request without a preflight, and the preflight asks
/// this API for permission it does not give. The cookie comparison is the
/// second half — it is what stops a request carrying some other origin's token,
/// or a stale one.
///
/// What it does NOT defend against, stated rather than implied: somebody able
/// to write cookies on this domain — a subdomain they control, or plain http
/// on a shared network — can set both halves. <c>Secure</c> in production
/// closes the second; there are no subdomains today. The session cookie is
/// also SameSite=Lax, which already blocks the plain cross-site form post, so
/// this is a second lock rather than the only one.
/// </remarks>
public static class Csrf
{
    /// <summary>Angular's default. Readable by JavaScript on purpose.</summary>
    public const string CookieName = "XSRF-TOKEN";

    /// <summary>Angular's default. What HttpXsrfInterceptor copies the cookie into.</summary>
    public const string HeaderName = "X-XSRF-TOKEN";

    /// <summary>
    /// Methods that may change something, and so have to prove they were meant.
    /// </summary>
    /// <remarks>
    /// GET and HEAD are absent because Angular does not sign them, and because
    /// a GET that changes data is the actual bug in that case. OPTIONS is
    /// absent because refusing a preflight would refuse the request after it.
    /// </remarks>
    private static readonly HashSet<string> Guarded =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public static bool Guards(string method) => Guarded.Contains(method);

    /// <summary>32 bytes of CSPRNG, base64url — survives a cookie and a header unencoded.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Whether the two halves match.
    /// </summary>
    /// <remarks>
    /// Fixed-time, not because the token is a password but because there is no
    /// reason to hand out a length-prefix oracle for free.
    /// </remarks>
    public static bool Match(string? cookie, string? header)
    {
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header)) return false;

        var a = Encoding.UTF8.GetBytes(cookie);
        var b = Encoding.UTF8.GetBytes(header);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// Issues the token cookie, and refuses a mutating request that does not echo it.
/// </summary>
/// <remarks>
/// Middleware rather than a check in each handler, and for the opposite reason
/// the admin gate is a function each route calls: a handler that forgets a
/// guard is unprotected and looks fine, where a route that wants out of this
/// has to be named here, which is a visible edit. Sixty-odd endpoints exist
/// and more arrive; the one that gets forgotten is the whole vulnerability.
///
/// The cookie goes out with every response rather than from one endpoint,
/// because Angular sends the header only when the cookie is already there. An
/// app that posted before it had ever made a GET would send nothing and be
/// refused — an occasional 403 on a cold page, reproducible by nobody.
/// </remarks>
public sealed class CsrfMiddleware(RequestDelegate next, IHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var existing = context.Request.Cookies[Csrf.CookieName];

        if (Csrf.Guards(context.Request.Method))
        {
            var header = context.Request.Headers[Csrf.HeaderName].ToString();

            if (!Csrf.Match(existing, header))
            {
                // Deliberately not "your session expired". This is not about
                // who the caller is — a signed-in admin gets it too if the
                // header is missing — and sending them to sign in again would
                // send them somewhere that does not fix it.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "This request was missing its cross-site protection token. "
                          + "Reload the page and try again.",
                });
                return;
            }
        }

        if (existing is null)
        {
            // Issued once and kept. Rotating per response would break two
            // requests in flight together: the second would carry the token
            // the first had already replaced.
            context.Response.Cookies.Append(Csrf.CookieName, Csrf.NewToken(), new CookieOptions
            {
                // The one cookie here JavaScript must be able to read. The
                // session cookie is HttpOnly precisely so a script cannot get
                // at it; this one is useless unless a script can, because the
                // mechanism IS the client copying it into a header. It is not
                // a secret — it is a value only this site's own pages can see,
                // which is a different property.
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Secure = env.IsProduction(),
                Path = "/",
                // A day. Long enough not to expire behind an open tab, short
                // enough that one does not follow somebody around forever.
                MaxAge = TimeSpan.FromDays(1),
            });
        }

        await next(context);
    }
}
