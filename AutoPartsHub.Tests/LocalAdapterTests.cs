using AutoPartsHub.Application.Abstractions;
using AutoPartsHub.Infrastructure.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoPartsHub.Tests;

/// <summary>
/// The local stand-ins for the things this deployment does not have.
/// </summary>
/// <remarks>
/// There is no TecDoc subscription, no Odoo, no Redis and no SMTP server, and
/// waiting for them would mean the import pipeline, the outbox and the price
/// cache could not be built at all. These are what the code around them is
/// written against.
///
/// The behaviour asserted below is the behaviour a caller has to be written
/// for, not the behaviour that is convenient. A fake that issued a fresh id
/// on every send, or a cache that forgot on a version bump, would let code be
/// written that breaks the first time the real one is plugged in — which is
/// the only way a fake can actively cost something.
/// </remarks>
public class LocalAdapterTests
{
    // ------------------------------------------------------------- files

    private static LocalDiskFileStore StoreIn(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "aph-tests", Guid.NewGuid().ToString("n"));
        return new LocalDiskFileStore(root);
    }

    private static MemoryStream Bytes(string text) =>
        new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task AFileComesBackAsItWentIn()
    {
        var store = StoreIn(out var root);
        try
        {
            var saved = await store.SaveAsync(Bytes("part,price\nABC,10"), "prices.csv");

            await using var back = await store.OpenAsync(saved.Id);
            Assert.NotNull(back);
            Assert.Equal("part,price\nABC,10", await new StreamReader(back!).ReadToEndAsync());
            Assert.Equal("prices.csv", saved.OriginalName);
            Assert.Equal(17, saved.Bytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <remarks>
    /// The whole of the path safety. What arrives from an upload form is
    /// attacker-controlled text, so nothing built from it reaches a path — the
    /// id is generated and the name is kept beside the file instead.
    /// </remarks>
    [Fact]
    public async Task TheNameTheUploaderChoseNeverReachesThePath()
    {
        var store = StoreIn(out var root);
        try
        {
            var saved = await store.SaveAsync(Bytes("x"), "../../etc/passwd");

            Assert.Equal(32, saved.Id.Length);
            Assert.All(saved.Id, c => Assert.True(char.IsAsciiHexDigitLower(c)));
            // Everything written is directly inside the root, whatever was asked for.
            foreach (var written in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                Assert.Equal(root, Path.GetDirectoryName(written));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <remarks>
    /// An id arrives back through a URL, so it is caller input again by the
    /// time it is used to open something. Anything that is not a well-formed id
    /// is answered as "no such file" rather than reaching the filesystem.
    /// </remarks>
    [Theory]
    [InlineData("../../../Windows/win.ini")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("NOTHEXADECIMAL0000000000000000000")]
    [InlineData("ABCDEF01234567890ABCDEF012345678")]  // upper case: not one of ours
    public async Task AnIdThatCouldNotHaveBeenIssuedOpensNothing(string id)
    {
        var store = StoreIn(out var root);
        try
        {
            Assert.Null(await store.OpenAsync(id));
            await store.DeleteAsync(id);   // and does not throw
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DeletingSomethingAlreadyGoneIsFine()
    {
        var store = StoreIn(out var root);
        try
        {
            var saved = await store.SaveAsync(Bytes("x"), "x.txt");

            await store.DeleteAsync(saved.Id);
            await store.DeleteAsync(saved.Id);

            Assert.Null(await store.OpenAsync(saved.Id));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------- cache

    [Fact]
    public async Task APriceComesBackUntilItIsAskedForTooLate()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new InMemoryPriceCache(clock);

        await cache.SetAsync("k", 801.54m, TimeSpan.FromHours(1));
        Assert.Equal(801.54m, await cache.GetAsync("k"));

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(801.54m, await cache.GetAsync("k"));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await cache.GetAsync("k"));
    }

    /// <remarks>
    /// The version is the invalidation. Finding every key a rule change
    /// affected would mean walking the keyspace, which is the operation nobody
    /// may run against a live Redis — so the key carries the version and a bump
    /// moves all of them out of reach at once.
    /// </remarks>
    [Fact]
    public async Task ABumpMovesEveryKeyOutOfReachWithoutDeletingAnything()
    {
        var cache = new InMemoryPriceCache();

        var before = await cache.VersionAsync();
        await cache.SetAsync($"{before}:art:sup", 10m, TimeSpan.FromHours(1));

        await cache.BumpVersionAsync();
        var after = await cache.VersionAsync();

        Assert.NotEqual(before, after);
        // Nothing was swept: the old entry is still held, and simply is not
        // what anybody asks for any more.
        Assert.Null(await cache.GetAsync($"{after}:art:sup"));
        Assert.Equal(10m, await cache.GetAsync($"{before}:art:sup"));
    }

    [Fact]
    public async Task AMissIsNullAndNotAnError()
    {
        Assert.Null(await new InMemoryPriceCache().GetAsync("never-set"));
    }

    // -------------------------------------------------------------- odoo

    /// <remarks>
    /// The behaviour the outbox depends on. It retries whenever a reply is
    /// lost, and a client that issued a new sales order per attempt would
    /// duplicate one the first time the real system timed out — a fake that did
    /// not hold this would hide that until production.
    /// </remarks>
    [Fact]
    public async Task SendingTheSameOrderTwiceRaisesOneSalesOrder()
    {
        var odoo = new FakeOdooClient(NullLogger<FakeOdooClient>.Instance);

        var first = await odoo.SendOrderAsync("ord-1");
        var again = await odoo.SendOrderAsync("ord-1");

        Assert.Equal(first.OdooOrderId, again.OdooOrderId);
        Assert.Single(odoo.Sent);
    }

    [Fact]
    public async Task AnOrderStartsSomewhereThePollerCanFindIt()
    {
        var odoo = new FakeOdooClient(NullLogger<FakeOdooClient>.Instance);
        var sent = await odoo.SendOrderAsync("ord-1");

        var states = await odoo.GetStatesAsync([sent.OdooOrderId]);
        Assert.Equal("draft", Assert.Single(states).State);

        odoo.SetState(sent.OdooOrderId, "sale");
        states = await odoo.GetStatesAsync([sent.OdooOrderId]);
        Assert.Equal("sale", Assert.Single(states).State);
    }

    /// <remarks>
    /// Left out, not reported in some invented state. A poller that read
    /// "unknown" as a real state would act on it.
    /// </remarks>
    [Fact]
    public async Task AnIdItNeverIssuedIsNotAnsweredFor()
    {
        var odoo = new FakeOdooClient(NullLogger<FakeOdooClient>.Instance);
        await odoo.SendOrderAsync("ord-1");

        Assert.Empty(await odoo.GetStatesAsync(["SO99999"]));
    }

    // ------------------------------------------------------------ tecdoc

    /// <remarks>
    /// Part numbers are typed with whatever separators the person in front of
    /// the keyboard used — `0 986 424 815` off a box, `0986424815` off an
    /// invoice. Normalising is the one piece of behaviour the callers have to
    /// be written for, so the fake has it.
    /// </remarks>
    [Theory]
    [InlineData("0986424815")]
    [InlineData("0 986 424 815")]
    [InlineData("0986-424-815")]
    [InlineData("0986424815 ")]
    public async Task AnArticleIsFoundHoweverItsNumberWasTyped(string typed)
    {
        var found = await new FakeTecDocClient().FindArticleAsync("bosch", typed);

        Assert.NotNull(found);
        Assert.Equal("Brake pad set, disc brake", found!.Name);
    }

    [Fact]
    public async Task ADifferentBrandWithTheSameNumberIsADifferentPart()
    {
        Assert.Null(await new FakeTecDocClient().FindArticleAsync("HELLA", "0986424815"));
    }

    /// <remarks>
    /// One OE number reaching two brands is the entire point of a
    /// cross-reference: search an OE number, be sold an aftermarket part.
    /// </remarks>
    [Fact]
    public async Task AnOeNumberReachesEveryBrandThatAnswersToIt()
    {
        var found = await new FakeTecDocClient().FindByOeNumberAsync("650311");

        Assert.Equal(2, found.Count);
        Assert.Contains(found, a => a.Manufacturer == "BOSCH");
        Assert.Contains(found, a => a.Manufacturer == "MANN-FILTER");
    }

    [Fact]
    public async Task AnUnknownOeNumberReachesNothing()
    {
        Assert.Empty(await new FakeTecDocClient().FindByOeNumberAsync("not-a-number"));
    }

    // -------------------------------------------------------------- mail

    [Fact]
    public async Task TheNoopSenderSendsNothingAndDoesNotThrow()
    {
        IEmailSender sender = new NoopEmailSender(NullLogger<NoopEmailSender>.Instance);

        await sender.SendAsync("someone@example.invalid", "Your order", "body");
    }
}

/// <summary>A clock the tests move by hand.</summary>
/// <remarks>
/// Small enough to keep here rather than take a package for it. The cache is
/// the only thing in this repository that reads a clock through
/// <see cref="TimeProvider"/>, and testing an expiry any other way means a
/// test that sleeps.
/// </remarks>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
