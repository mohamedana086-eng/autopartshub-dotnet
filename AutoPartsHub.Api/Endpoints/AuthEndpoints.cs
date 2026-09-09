using AutoPartsHub.Api.Auth;
using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
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

        // POST /api/auth/register { name, email, password, role, city }
        app.MapPost("/api/auth/register", async (
            RegisterRequest body, AutoPartsContext db, SessionTokens tokens,
            HttpContext http, IHostEnvironment env, CancellationToken ct) =>
        {
            var name = (body.Name ?? "").Trim();
            var email = (body.Email ?? "").Trim().ToLowerInvariant();
            var password = body.Password ?? "";
            var role = body.Role ?? Roles.Retail;
            var city = (body.City ?? "").Trim() is { Length: > 0 } c ? c : null;

            if (name.Length == 0 || email.Length == 0 || password.Length == 0)
            {
                return Results.BadRequest(new { error = "Please fill in all fields." });
            }
            if (password.Length < 6)
            {
                return Results.BadRequest(new { error = "Password must be at least 6 characters." });
            }
            // Self-registration cannot mint an admin, whatever the request body
            // says. Compared against the two literals rather than run through
            // Roles.Narrow, which would quietly turn "ADMIN" into "RETAIL" and
            // open the account instead of refusing it.
            if (role is not (Roles.B2B or Roles.Retail))
            {
                return Results.BadRequest(new { error = "Invalid account type." });
            }

            if (await db.Clients.AnyAsync(x => x.Email == email, ct))
            {
                return Results.Json(
                    new { error = "An account with this email already exists." }, statusCode: 409);
            }

            // New accounts start on the Retail tier. B2B applicants are
            // reviewed by an admin from /admin/clients and moved onto a
            // negotiated tier later.
            var retailTier = await db.ClientCategories
                .Where(x => x.Name == "Retail").Select(x => x.Id).FirstOrDefaultAsync(ct);

            // Ten rounds, because that is the cost the accounts already in the
            // table were hashed at and the two APIs share one login. The
            // library's own default is eleven.
            var hash = BCrypt.Net.BCrypt.HashPassword(password, BcryptRounds);

            var id = Ids.New();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Client" ("id", "name", "email", "city", "role", "passwordHash", "categoryId")
                VALUES ({id}, {name}, {email}, {city}, {role}, {hash}, {retailTier})
                """, ct);

            Issue(http, tokens, env, new SessionPayload(
                id, Roles.Narrow(role), retailTier, name,
                DateTimeOffset.UtcNow.Add(SessionTokens.MaxAge).ToUnixTimeMilliseconds()));

            return Results.Json(
                new { user = new { id, name, email, role = Roles.Narrow(role) } },
                statusCode: 201);
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

            var confirmedAt = await db.Clients
                .Where(c => c.Id == session.UserId)
                .Select(c => c.EmailConfirmedAt)
                .FirstOrDefaultAsync();

            return Results.Ok(new
            {
                user = new
                {
                    id = session.UserId,
                    name = session.Name,
                    role = session.Role,
                    tierName = tier ?? "Retail",
                    // Read rather than carried in the cookie, for the same
                    // reason the tier is: a session issued this morning
                    // predates a confirmation made this afternoon, and the
                    // banner asking them to confirm would sit there until the
                    // cookie was next reissued.
                    //
                    // A boolean rather than the date. Nothing renders when it
                    // happened, and sending it would put an account's
                    // timestamps in front of anyone who opened the network tab
                    // for no reason anybody asked for.
                    emailConfirmed = confirmedAt is not null,
                },
            });
        });
    }

    /// <summary>The cost the accounts already in the table were hashed at.</summary>
    internal const int BcryptRounds = 10;

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

    /// <summary>Sets the session cookie. Internal to the assembly: the supplier
    /// signup issues one too, and two copies of these flags would drift.</summary>
    internal static void Issue(HttpContext http, SessionTokens tokens, IHostEnvironment env, SessionPayload payload)
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

public record RegisterRequest(
    string? Name, string? Email, string? Password, string? Role, string? City);
