using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Health;
using AutoPartsHub.Application.Abstractions;
using AutoPartsHub.Infrastructure.Local;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// Whether an instance says it can serve.
/// </summary>
/// <remarks>
/// The check worth having is not "does it answer 200 when everything is fine"
/// — that passes on an endpoint that returns 200 unconditionally, which is
/// most of the readiness probes ever written. It is the other three:
///
/// - unreadiness when the database is unreachable;
/// - unreadiness when the database answers but its schema is behind the code,
///   which is the state a deployment is in between starting and finishing its
///   migrations, and the one a load balancer must not send traffic into;
/// - READINESS when something that is only reported is broken, because a probe
///   that fails on those takes the shop down to save it from being slow.
///
/// All three need a real engine: the first two are about what a connection
/// does, and the third is about a check that runs against one.
/// </remarks>
[Collection(CatalogueCollection.Name)]
public class ReadinessTests(Catalogue catalogue)
{
    private static Readiness Probe(AutoPartsContext db, IPriceCache? cache = null) =>
        new(db, cache ?? new InMemoryPriceCache(), FullText(), new Environment("Development"));

    private static FullTextSearch FullText()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AutoPartsContext(
            new DbContextOptionsBuilder<AutoPartsContext>()
                .UseSqlServer(SqlServer.ConnectionString).Options));

        return new FullTextSearch(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>An IHostEnvironment, which only the mail line reads.</summary>
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    // ------------------------------------------------------------ the happy one

    [SqlServerFact]
    public async Task AnInstanceWithItsSchemaAppliedIsReady()
    {
        var report = await Probe(catalogue.Db).CheckAsync();

        Assert.True(report.Ready, Failed(report));
        Assert.All(report.Checks.Where(c => c.Required), c => Assert.True(c.Ok, c.Detail));
    }

    /// <remarks>
    /// Named rather than counted, because a probe that stopped checking the
    /// schema would still be "ready" and still pass a test that only counted.
    /// </remarks>
    [SqlServerFact]
    public async Task ItSaysWhatItChecked()
    {
        var report = await Probe(catalogue.Db).CheckAsync();
        var names = report.Checks.Select(c => c.Name).ToArray();

        Assert.Equal(["database", "schema", "cache", "fullText", "mail"], names);

        // Two decide; the rest are reported.
        Assert.Equal(
            ["database", "schema"],
            report.Checks.Where(c => c.Required).Select(c => c.Name));
    }

    [SqlServerFact]
    public async Task TheDatabaseCheckNamesTheEngine()
    {
        var report = await Probe(catalogue.Db).CheckAsync();

        Assert.Contains("sqlserver", Detail(report, "database"));
        Assert.Contains("none pending", Detail(report, "schema"));
    }

    // ------------------------------------------------------- the useful ones

    /// <summary>
    /// A database that is not there.
    /// </summary>
    /// <remarks>
    /// Pointed at an instance that does not exist, with a one-second connect
    /// timeout so the test is not the slowest in the suite. The probe has to
    /// come back with an answer rather than with the wait.
    /// </remarks>
    [SqlServerFact]
    public async Task AnInstanceThatCannotReachItsDatabaseIsNotReady()
    {
        using var db = new AutoPartsContext(new DbContextOptionsBuilder<AutoPartsContext>()
            .UseSqlServer("Server=(localdb)\\NoSuchInstance;Database=Nothing;"
                        + "Trusted_Connection=True;Connect Timeout=1")
            .Options);

        var report = await Probe(db).CheckAsync();

        Assert.False(report.Ready);
        Assert.False(report.Checks.Single(c => c.Name == "database").Ok);
        // And it says what happened rather than only that something did.
        Assert.NotEmpty(Detail(report, "database"));
    }

    /// <summary>
    /// A database that answers, with a schema behind the code.
    /// </summary>
    /// <remarks>
    /// The state a deployment is in between starting and finishing its
    /// migrations, and the reason this endpoint is not just a connection test.
    /// Provoked by taking a migration back out of the history table rather
    /// than by mocking anything — what is being asserted is that EF and this
    /// probe agree about what "pending" means.
    /// </remarks>
    [SqlServerFact]
    public async Task AnInstanceWhoseSchemaIsBehindTheCodeIsNotReady()
    {
        var last = (await catalogue.Db.Database.GetAppliedMigrationsAsync()).Last();

        await catalogue.Db.Database.ExecuteSqlAsync($"""
            DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = {last}
            """);
        try
        {
            var report = await Probe(catalogue.Db).CheckAsync();

            Assert.False(report.Ready);
            Assert.True(report.Checks.Single(c => c.Name == "database").Ok);
            Assert.False(report.Checks.Single(c => c.Name == "schema").Ok);
            Assert.Contains(last, Detail(report, "schema"));
        }
        finally
        {
            await catalogue.Db.Database.ExecuteSqlAsync($"""
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ({last}, '10.0.11')
                """);
        }
    }

    /// <summary>
    /// A broken cache does not take the instance out of rotation.
    /// </summary>
    /// <remarks>
    /// IPriceCache says it plainly: "a cache that is down is a slow shop, and
    /// a cache that is down and throwing is a closed one". A readiness probe
    /// that failed on it would close the shop to save it from being slow, and
    /// it would do so on the day Redis is introduced rather than today — which
    /// is why this is asserted now, while the only implementation cannot fail.
    /// </remarks>
    [SqlServerFact]
    public async Task ACacheThatIsDownIsReportedAndDoesNotDecide()
    {
        var report = await Probe(catalogue.Db, new BrokenCache()).CheckAsync();

        Assert.True(report.Ready, Failed(report));
        Assert.False(report.Checks.Single(c => c.Name == "cache").Ok);
        Assert.Contains("nothing is listening", Detail(report, "cache"));
    }

    private sealed class BrokenCache : IPriceCache
    {
        public Task<long> VersionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("nothing is listening on the cache");

        public Task BumpVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<decimal?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<decimal?>(null);
        public Task SetAsync(string key, decimal price, TimeSpan ttl, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A dependency that hangs becomes an answer, not a hang.
    /// </summary>
    /// <remarks>
    /// The failure a readiness probe is most likely to have, and the worst:
    /// the host waits on it, the instance stays in the rotation, and requests
    /// queue behind the same dependency the probe is stuck on. Each check has
    /// a two-second budget, so this returns in about that rather than in the
    /// minute the cache would otherwise take.
    /// </remarks>
    [SqlServerFact]
    public async Task ADependencyThatHangsIsCountedAsFailedRatherThanWaitedFor()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var report = await Probe(catalogue.Db, new HangingCache()).CheckAsync();

        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
        Assert.False(report.Checks.Single(c => c.Name == "cache").Ok);
        Assert.Contains("no answer within", Detail(report, "cache"));
        // Still ready: the cache does not decide.
        Assert.True(report.Ready, Failed(report));
    }

    private sealed class HangingCache : IPriceCache
    {
        public async Task<long> VersionAsync(CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            return 0;
        }

        public Task BumpVersionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<decimal?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<decimal?>(null);
        public Task SetAsync(string key, decimal price, TimeSpan ttl, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static string Detail(Readiness.Report report, string name) =>
        report.Checks.Single(c => c.Name == name).Detail;

    private static string Failed(Readiness.Report report) =>
        string.Join("; ", report.Checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}"));
}
