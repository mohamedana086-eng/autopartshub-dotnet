using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Tests.SqlServer;

/// <summary>
/// A small catalogue with known answers, on a real SQL Server.
/// </summary>
/// <remarks>
/// The dialect check settled whether SQL Server ACCEPTS the ported statements.
/// It cannot say whether they still mean the same thing: a LIMIT rewritten to
/// a TOP that lost its ORDER BY parses perfectly and returns different rows,
/// and an OPENJSON list compared under the wrong collation matches nothing at
/// all while looking entirely correct. Only data and assertions catch that.
///
/// So the numbers here are chosen to make a wrong answer visible rather than
/// plausible. Three suppliers at three priorities with prices that disagree
/// with those priorities, so "cheapest" and "preferred" pick different rows.
/// Part numbers whose normalised forms collide across brands. Enough rows to
/// page, in an order that is not the insertion order.
///
/// SHARED, AND REBUILT ONCE
/// ------------------------
/// One fixture for the whole class, built once: applying migrations takes
/// longer than every assertion in the file put together. The tests only read,
/// so sharing is safe — the ones that write get their own rows and clean up
/// after themselves.
/// </remarks>
public sealed class Catalogue : IAsyncLifetime
{
    public AutoPartsContext Db { get; private set; } = null!;

    /// <summary>Null when there is a database and these tests ran; the reason otherwise.</summary>
    public string? Unavailable => SqlServer.Unavailable;

    public async Task InitializeAsync()
    {
        if (Unavailable is not null) return;

        Db = new AutoPartsContext(new DbContextOptionsBuilder<AutoPartsContext>()
            .UseSqlServer(SqlServer.ConnectionString)
            .Options);

        // The schema comes from the migrations, not from EnsureCreated: the
        // migrations are what a deployment runs, and a test that built the
        // schema another way would not be testing the schema that ships. It is
        // also the only thing that creates the BestOffer view.
        await Db.Database.MigrateAsync();

        await EmptyAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Db?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    /// <summary>
    /// Every table, in an order that does not fight the foreign keys.
    /// </summary>
    /// <remarks>
    /// Deleted rather than truncated: TRUNCATE will not touch a table a
    /// foreign key points at, which is most of them.
    /// </remarks>
    private async Task EmptyAsync()
    {
        foreach (var table in new[]
        {
            "OrderItemAllocation", "OrderItem", "Order", "CartItem", "Cart",
            "TicketMessage", "Ticket", "PriceListImportRow", "PriceListImport",
            "PriceListItem", "PriceList", "SupplierOffer", "ProductBarcode",
            "ProductSpec", "ProductImage", "StockLevel", "Fitment", "Interchange",
            "MarkupRuleCondition", "MarkupRule", "SearchMiss", "VinLookup",
            "Product", "GoodsCategory", "Manufacturer", "VehicleVariant",
            "VehicleModel", "VehicleMake", "VehicleSystem", "Notification",
            "VerificationToken", "Client", "Supplier", "RetailOutlet", "Warehouse",
            "ClientCategory", "Currency",
        })
        {
            await Db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\"");
        }
    }

    // The ids every test reads by. Named rather than looked up, so an assertion
    // says which row it is about.
    public const string Preferred = "sup-preferred";
    public const string Cheapest = "sup-cheapest";
    public const string Stopped = "sup-stopped";
    public const string BrakePad = "prd-brake-pad";
    public const string OilFilter = "prd-oil-filter";
    public const string Retail = "cat-retail";

    private async Task SeedAsync()
    {
        Db.Currencies.Add(new Currency
        {
            Id = "cur-eur", Code = "EUR", Name = "Euro", Symbol = "€",
            Rate = 1, IsBase = true, Active = true,
        });

        Db.ClientCategories.Add(new ClientCategory
        {
            Id = Retail, Name = "Retail", MarkupPercent = 40,
            MinOrderAmount = 0, RequiresApproval = false, ShelfLifeDays = 0,
        });

        Db.VehicleSystems.Add(new VehicleSystem
        {
            Id = "sys-brakes", Name = "Brakes", Slug = "brakes", Icon = "disc", Order = 1,
        });

        Db.Manufacturers.AddRange(
            new Manufacturer { Id = "man-bosch", Name = "BOSCH", IsOem = false },
            new Manufacturer { Id = "man-mann", Name = "MANN-FILTER", IsOem = false });

        // Three suppliers whose priority and price disagree on purpose: the
        // preferred one is the DEARER of the two live ones, so a ranking that
        // sorted by price alone would pick the other and look reasonable.
        Db.Suppliers.AddRange(
            new Supplier
            {
                Id = Preferred, Name = "Preferred Parts", Code = "PREF", Slug = "preferred-parts",
                Reliability = "official", Active = true, Priority = 10, Rating = 5,
                AcceptsReturns = true, MinOrderAmount = 0,
            },
            new Supplier
            {
                Id = Cheapest, Name = "Cheapest Parts", Code = "CHEA", Slug = "cheapest-parts",
                Reliability = "standard", Active = true, Priority = 0, Rating = 3,
                AcceptsReturns = false, MinOrderAmount = 0,
            },
            new Supplier
            {
                Id = Stopped, Name = "Stopped Parts", Code = "STOP", Slug = "stopped-parts",
                Reliability = "standard", Active = false, Priority = 99, Rating = 4,
                MinOrderAmount = 0,
            });

        // Part numbers that normalise onto each other across brands, which is
        // what the bulk lookup and the search both have to get right.
        Db.Products.AddRange(
            new Product
            {
                Id = BrakePad, PartNumber = "0 986 424 815", Name = "Brake pad set, front",
                ManufacturerId = "man-bosch", VehicleSystemId = "sys-brakes",
                BasePrice = 100, Currency = "EUR", StockDays = 2, CreatedAt = DateTime.UtcNow,
                PackagingUnit = "set", PartType = "aftermarket", QuantityPerPackage = 1,
            },
            new Product
            {
                Id = OilFilter, PartNumber = "W712/30", Name = "Oil filter",
                ManufacturerId = "man-mann", VehicleSystemId = "sys-brakes",
                BasePrice = 10, Currency = "EUR", StockDays = 1, CreatedAt = DateTime.UtcNow,
                PackagingUnit = "piece", PartType = "aftermarket", QuantityPerPackage = 1,
            });

        await Db.SaveChangesAsync();

        // Offers last: they reference both sides. The stopped supplier's offer
        // is the cheapest of all AND the highest priority, so any ranking that
        // forgot to exclude them would visibly pick it.
        Db.SupplierOffers.AddRange(
            new SupplierOffer
            {
                Id = "off-pref", ProductId = BrakePad, SupplierId = Preferred,
                PurchasePrice = 60, Active = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new SupplierOffer
            {
                Id = "off-cheap", ProductId = BrakePad, SupplierId = Cheapest,
                PurchasePrice = 50, Active = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new SupplierOffer
            {
                Id = "off-stopped", ProductId = BrakePad, SupplierId = Stopped,
                PurchasePrice = 10, Active = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });

        await Db.SaveChangesAsync();
    }
}

/// <summary>Marks the tests that share one <see cref="Catalogue"/>.</summary>
[CollectionDefinition(Name)]
public sealed class CatalogueCollection : ICollectionFixture<Catalogue>
{
    public const string Name = "seeded catalogue";
}
