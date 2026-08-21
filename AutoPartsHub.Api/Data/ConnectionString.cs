namespace AutoPartsHub.Api.Data;

/// <summary>
/// Resolves the database connection string from configuration.
/// </summary>
/// <remarks>
/// Accepts the <c>postgresql://user:pass@host/db</c> URL that Neon, Vercel,
/// Railway, Fly and Heroku all hand out, as well as Npgsql's own
/// <c>Host=…;Database=…</c> form. The URL is the shape you get by copying a
/// value out of a dashboard, so it is the shape that has to work — converting
/// it by hand once per environment is a step someone eventually gets wrong.
///
/// Read at startup and not before: nothing here reaches for a database while
/// the assembly is loading, so a build or a unit test needs no connection
/// string at all.
/// </remarks>
public static class ConnectionString
{
    /// <summary>Environment variables to try, in order, before configuration.</summary>
    private static readonly string[] Names = ["DATABASE_URL", "DIRECT_URL"];

    /// <summary>
    /// Query parameters that libpq understands and Npgsql does not, because
    /// Npgsql reaches the same end another way.
    /// </summary>
    /// <remarks>
    /// Neon's connection strings carry <c>channel_binding=require</c>. libpq
    /// takes it as an instruction; Npgsql negotiates SCRAM-SHA-256-PLUS on its
    /// own whenever the server offers it over TLS, so the protection is there
    /// either way and passing the key through only produces an argument error
    /// at startup. Listed rather than caught, so a genuine typo in a
    /// connection string still fails loudly.
    /// </remarks>
    private static readonly HashSet<string> LibpqOnly =
        new(StringComparer.OrdinalIgnoreCase) { "channel_binding", "target_session_attrs", "gssencmode" };

    public static string Resolve(IConfiguration configuration)
    {
        var raw = Names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                  ?? configuration.GetConnectionString("Default");

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "No database connection configured. Set DATABASE_URL, or ConnectionStrings:Default.");
        }

        return Normalise(raw);
    }

    /// <summary>Turns a postgres URL into Npgsql's key/value form. Anything
    /// that is not a URL is already in that form and passes through.</summary>
    public static string Normalise(string raw)
    {
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        var uri = new Uri(raw);
        var credentials = uri.UserInfo.Split(':', 2);

        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,
            // Neon terminates TLS at its proxy and presents a certificate the
            // platform trusts, so Require is right and VerifyFull would need a
            // root certificate shipped alongside the app.
            SslMode = Npgsql.SslMode.Require,
        };

        // Anything the url carried as a query parameter wins over the defaults
        // above, so ?sslmode=verify-full is not silently dropped.
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in query.AllKeys.Where(k => k is not null).Select(k => k!))
        {
            if (LibpqOnly.Contains(key)) continue;

            // Not swallowed in a catch: an unknown key here is a typo in a
            // connection string, and finding that out at startup with the key
            // named is better than finding out later that a setting never
            // applied.
            builder[key] = query[key];
        }

        return builder.ConnectionString;
    }
}
