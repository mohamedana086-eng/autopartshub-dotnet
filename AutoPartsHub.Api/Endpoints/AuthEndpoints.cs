using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/auth/login { email, password }
        app.MapPost("/api/auth/login", async (
            LoginRequest body, AutoPartsContext db, SessionTokens tokens, HttpContext http, IHostEnvironment env) =>
        {
            var email = (body.Email ?? "").Trim().ToLowerInvariant();
            var password = body.Password ?? "";

            if (email.Length == 0 || password.Length == 0)
            {
                return Results.BadRequest(new { error = "Please enter your email and password." });
            }

            var client = await db.Clients
                .Where(c => c.Email.ToLower() == email)
                .Select(c => new { c.Id, c.Email, c.Name, c.Role, c.CategoryId, c.PasswordHash })
                .AsNoTracking()
                .FirstOrDefaultAsync();

            // One message and one shape for "no such account" and "wrong
            // password" alike: telling them apart tells an attacker which
            // addresses are registered. The hash is still verified when the
            // account is missing so the two paths take the same time.
            var hash = client?.PasswordHash;
            var correct = BCrypt.Net.BCrypt.Verify(password, hash ?? BcryptDecoy);

            if (client is null || hash is null || !correct)
            {
                return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);
            }

            Issue(http, tokens, env, new SessionPayload(
                client.Id, Roles.Narrow(client.Role), client.CategoryId, client.Name,
                DateTimeOffset.UtcNow.Add(SessionTokens.MaxAge).ToUnixTimeMilliseconds()));

            // Field order matches the original response, not C# habit: the two
            // APIs are compared by serialising both and diffing the text, and a
            // reordered object reads as a difference every time.
            return Results.Ok(new
            {
                user = new { id = client.Id, name = client.Name, email = client.Email, role = Roles.Narrow(client.Role) },
            });
        });

        // POST /api/auth/logout
        app.MapPost("/api/auth/logout", (HttpContext http, IHostEnvironment env) =>
        {
            http.Response.Cookies.Append(SessionTokens.CookieName, "", new CookieOptions
            {
                Path = "/",
                MaxAge = TimeSpan.Zero,
            });
            return Results.Ok(new { ok = true });
        });

        // GET /api/auth/session — who the caller is, plus the tier their prices use.
        app.MapGet("/api/auth/session", async (HttpContext http, SessionTokens tokens, AutoPartsContext db) =>
        {
            var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);
            if (session is null) return Results.Ok(new { user = (object?)null });

            // The tier is read rather than carried in the cookie, so a tier
            // renamed this morning shows this afternoon instead of whenever
            // the cookie is next reissued.
            var tier = session.CategoryId is null
                ? null
                : await db.ClientCategories
                    .Where(c => c.Id == session.CategoryId)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync();

            return Results.Ok(new
            {
                user = new
                {
                    id = session.UserId,
                    name = session.Name,
                    role = session.Role,
                    tierName = tier ?? "Retail",
                },
            });
        });
    }

    /// <summary>
    /// A valid bcrypt hash of a value nothing will ever submit.
    /// </summary>
    /// <remarks>
    /// Verified against when no account matches, so a request for an unknown
    /// address costs the same as one for a known address with the wrong
    /// password. Without it the unknown case returns immediately and the
    /// difference is measurable, which turns the login into an oracle for
    /// which addresses are registered.
    /// </remarks>
    private const string BcryptDecoy = "$2a$10$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy";

    private static void Issue(HttpContext http, SessionTokens tokens, IHostEnvironment env, SessionPayload payload)
    {
        http.Response.Cookies.Append(SessionTokens.CookieName, tokens.Encode(payload), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = env.IsProduction(),
            MaxAge = SessionTokens.MaxAge,
            Path = "/",
        });
    }
}

public record LoginRequest(string? Email, string? Password);
