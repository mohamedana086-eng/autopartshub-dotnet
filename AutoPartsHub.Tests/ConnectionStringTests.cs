using AutoPartsHub.Api.Data;

namespace AutoPartsHub.Tests;

/// <summary>
/// Which engine a deployment is pointed at.
/// </summary>
/// <remarks>
/// Two providers for as long as the move takes, and exactly one line decides
/// which is used. Getting it wrong is not subtle at runtime — the provider
/// that cannot parse the string throws at startup — but the message it throws
/// is about a keyword it does not recognise, which reads as a typo in the
/// connection string rather than as the wrong engine. That is the sort of
/// thing somebody fixes by editing the string.
///
/// None of this needs a database: it is a decision about text.
/// </remarks>
public class ConnectionStringTests
{
    private const string Neon =
        "postgresql://user:pass@ep-cool-darkness.eu-central-1.aws.neon.tech/autoparts?sslmode=require";

    private const string SqlServer =
        "Server=(localdb)\\MSSQLLocalDB;Database=AutoPartsHub;Trusted_Connection=True";

    // ----------------------------------------------------------- sniffing

    [Theory]
    [InlineData(Neon)]
    [InlineData("postgres://user:pass@localhost/autoparts")]
    [InlineData("Host=localhost;Database=autoparts;Username=postgres")]
    public void APostgresStringIsRecognisedWithoutBeingTold(string connection) =>
        Assert.Equal(DatabaseProvider.PostgreSql, ConnectionString.ProviderFor(null, connection));

    [Theory]
    [InlineData(SqlServer)]
    [InlineData("Data Source=db.internal;Initial Catalog=AutoPartsHub;Integrated Security=true")]
    public void ASqlServerStringIsRecognisedWithoutBeingTold(string connection) =>
        Assert.Equal(DatabaseProvider.SqlServer, ConnectionString.ProviderFor(null, connection));

    /// <remarks>
    /// Npgsql accepts `Server=` as an alias for `Host=` and SQL Server uses it
    /// as its own, so a string carrying only that is genuinely ambiguous.
    /// Refused rather than guessed: a guess here picks an engine, and the
    /// wrong one reads somebody else's data or none.
    /// </remarks>
    [Fact]
    public void AnAmbiguousStringHasToBeSettledByHand()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => ConnectionString.ProviderFor(null, "Server=db;Database=autoparts"));

        Assert.Contains("DATABASE_PROVIDER", refused.Message);
    }

    // ------------------------------------------------------ being told

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mssql")]
    [InlineData("  SqlServer  ")]
    public void TheSettingIsReadHoweverItIsSpelled(string configured) =>
        Assert.Equal(DatabaseProvider.SqlServer, ConnectionString.ProviderFor(configured, SqlServer));

    [Theory]
    [InlineData("postgres")]
    [InlineData("postgresql")]
    [InlineData("npgsql")]
    public void AndSoIsTheOther(string configured) =>
        Assert.Equal(DatabaseProvider.PostgreSql, ConnectionString.ProviderFor(configured, Neon));

    /// <remarks>
    /// A value nothing recognises is refused rather than falling back. A typo
    /// that quietly reverted a deployment to the old engine would be found by
    /// somebody reading stale data, which is the worst way to find it.
    /// </remarks>
    [Fact]
    public void AProviderNameNothingRecognisesIsRefused()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => ConnectionString.ProviderFor("postgres-ish", SqlServer));

        Assert.Contains("postgres-ish", refused.Message);
    }

    /// <summary>
    /// The setting and the string disagreeing — half a deployment moved.
    /// </summary>
    /// <remarks>
    /// The case this exists for: DATABASE_PROVIDER switched to sqlserver and
    /// DATABASE_URL still holding the Neon URL, or the reverse. Left to the
    /// provider it reports an unrecognised keyword, which is true and useless.
    /// </remarks>
    [Fact]
    public void ASettingThatDisagreesWithItsStringIsRefused()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => ConnectionString.ProviderFor("sqlserver", Neon));

        Assert.Contains("left over", refused.Message);

        var other = Assert.Throws<InvalidOperationException>(
            () => ConnectionString.ProviderFor("postgres", SqlServer));

        Assert.Contains("left over", other.Message);
    }

    /// <remarks>
    /// An ambiguous string is not a disagreement. `Server=` could be either, so
    /// the setting is taken at its word — which is the whole reason the setting
    /// exists.
    /// </remarks>
    [Fact]
    public void AnAmbiguousStringDefersToTheSetting()
    {
        const string ambiguous = "Server=db;Database=autoparts";

        Assert.Equal(DatabaseProvider.SqlServer, ConnectionString.ProviderFor("sqlserver", ambiguous));
        Assert.Equal(DatabaseProvider.PostgreSql, ConnectionString.ProviderFor("postgres", ambiguous));
    }

    // -------------------------------------------------------- normalising

    /// <remarks>
    /// The URL is the shape you get by copying a value out of a dashboard, so
    /// it is the shape that has to work. Npgsql does not read it.
    /// </remarks>
    [Fact]
    public void APostgresUrlBecomesKeyValuePairs()
    {
        var normalised = ConnectionString.Normalise(Neon);

        Assert.Contains("Host=ep-cool-darkness.eu-central-1.aws.neon.tech", normalised);
        Assert.Contains("Database=autoparts", normalised);
        Assert.Contains("Username=user", normalised);
        // And it still reads as PostgreSQL afterwards, which is what the
        // sniffing above depends on.
        Assert.Equal(DatabaseProvider.PostgreSql, ConnectionString.ProviderFor(null, normalised));
    }

    /// <remarks>
    /// Normalise converts one shape and passes everything else through. A SQL
    /// Server connection string is not a URL and must come out exactly as it
    /// went in — it is about to be handed to a provider that would reject any
    /// edit to it.
    /// </remarks>
    [Fact]
    public void ASqlServerStringIsNotTouched()
    {
        Assert.Equal(SqlServer, ConnectionString.Normalise(SqlServer));
    }

    /// <remarks>
    /// Neon's URLs carry channel_binding, which libpq takes as an instruction
    /// and Npgsql reaches another way — passing it through is an argument
    /// error at startup. Dropped rather than caught, so a genuine typo still
    /// fails loudly.
    /// </remarks>
    [Fact]
    public void TheParametersOnlyLibpqUnderstandsAreDropped()
    {
        var normalised = ConnectionString.Normalise(
            "postgresql://u:p@host/db?sslmode=require&channel_binding=require");

        Assert.DoesNotContain("channel_binding", normalised);
        Assert.Contains("SSL Mode=Require", normalised);
    }
}
