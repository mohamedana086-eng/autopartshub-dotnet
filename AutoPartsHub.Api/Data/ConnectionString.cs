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

    /// <summary>
    /// Which engine a connection string is for.
    /// </summary>
    /// <remarks>
    /// The platform is moving to SQL Server, and for as long as a move is in
    /// progress there are two answers rather than one. Getting it wrong is not
    /// a subtle failure — the provider that cannot parse the string throws at
    /// startup — but it is worth being explicit about, because the two formats
    /// overlap: Npgsql accepts <c>Server=</c> as an alias for <c>Host=</c>, so
    /// a string using it could plausibly be either.
    ///
    /// <paramref name="configured"/> — <c>DATABASE_PROVIDER</c> — settles it
    /// when it is set, and a value nothing recognises is refused rather than
    /// falling back: a typo that quietly reverted a deployment to the old
    /// engine would be found by somebody reading stale data.
    ///
    /// Sniffing is only for when it is not set, and only on the two shapes
    /// that are unambiguous. Everything currently deployed passes
    /// through <see cref="Normalise"/> first, which emits Npgsql's own
    /// <c>Host=</c> form, so today's configuration keeps working untouched.
    /// </remarks>
    public static DatabaseProvider ProviderFor(string? configured, string connectionString)
    {
        var looksLike = Sniff(connectionString);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return looksLike ?? throw new InvalidOperationException(
                "Could not tell which database this connection string is for. Set "
                + "DATABASE_PROVIDER to sqlserver or postgres.");
        }

        var chosen = configured.Trim().ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" => DatabaseProvider.SqlServer,
            "postgres" or "postgresql" or "npgsql" => DatabaseProvider.PostgreSql,
            _ => throw new InvalidOperationException(
                $"DATABASE_PROVIDER is \"{configured}\", which is not a provider this "
                + "application has. Use sqlserver or postgres."),
        };

        // A setting that disagrees with the string it is applied to.
        //
        // Refused here rather than left to the provider, because the provider's
        // answer is not a useful one: handing SQL Server a `Host=…;Database=…`
        // reports a keyword it does not recognise, which reads as a typo in the
        // connection string rather than as the wrong engine being selected.
        // Half a deployment is configured for the move and half is not, and
        // that is the sentence worth printing.
        if (looksLike is { } evident && evident != chosen)
        {
            throw new InvalidOperationException(
                $"DATABASE_PROVIDER says {chosen}, but the connection string is for {evident}. "
                + "One of the two is left over from before the move.");
        }

        return chosen;
    }

    /// <summary>
    /// Which engine a connection string looks like, or null when it is not
    /// one of the two unambiguous shapes.
    /// </summary>
    /// <remarks>
    /// `Server=` is deliberately not here: Npgsql accepts it as an alias for
    /// `Host=` and SQL Server uses it as its own, so a string carrying it is
    /// genuinely ambiguous and DATABASE_PROVIDER has to say.
    /// </remarks>
    private static DatabaseProvider? Sniff(string connectionString)
    {
        if (connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        {
            return DatabaseProvider.PostgreSql;
        }

        if (connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
        {
            return DatabaseProvider.SqlServer;
        }

        return null;
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

/// <summary>Which database engine is underneath.</summary>
/// <remarks>
/// Two, for as long as the move takes. The schema and the migrations in this
/// repository are SQL Server's; PostgreSQL is the engine the deployment is
/// still on, and its schema belongs to the Prisma migrations in the storefront
/// repository — which is why there is no migration here that would apply to it.
/// </remarks>
public enum DatabaseProvider
{
    PostgreSql,
    SqlServer,
}
