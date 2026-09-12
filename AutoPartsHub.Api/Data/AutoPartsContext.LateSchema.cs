using AutoPartsHub.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Data;

/// <summary>
/// Everything the scaffold never saw.
/// </summary>
/// <remarks>
/// The model in <c>AutoPartsContext.cs</c> was generated from the database
/// once and never regenerated. Eight migrations have landed on that database
/// since, and this application reads every one of the tables they added —
/// through raw SQL, which does not consult the model and so never complained.
///
/// Against PostgreSQL that was invisible, because the columns really are
/// there. It stopped being invisible the moment a schema had to be *generated*
/// from the model rather than read into it: what came out was missing eleven
/// tables and twenty-four columns that the application queries every day.
///
/// So this file is the catch-up, written from the migrations that added them:
/// <c>add_product_specs</c>, <c>add_barcodes_and_packaging</c>,
/// <c>add_goods_categories</c>, <c>vin_lookup_log</c>,
/// <c>price_list_imports</c>, <c>search_misses</c>, <c>support_tickets</c>,
/// <c>supplier_offers</c>, and the columns from the ones between.
///
/// It is kept apart from the generated file so that re-scaffolding does not
/// delete it, and together so that "what did the scaffold miss" has one
/// answer.
/// </remarks>
public partial class AutoPartsContext
{
    public virtual DbSet<ProductSpec> ProductSpecs { get; set; } = null!;

    public virtual DbSet<ProductBarcode> ProductBarcodes { get; set; } = null!;

    public virtual DbSet<GoodsCategory> GoodsCategories { get; set; } = null!;

    public virtual DbSet<VinLookup> VinLookups { get; set; } = null!;

    public virtual DbSet<SearchMiss> SearchMisses { get; set; } = null!;

    public virtual DbSet<SupplierOffer> SupplierOffers { get; set; } = null!;

    public virtual DbSet<Ticket> Tickets { get; set; } = null!;

    public virtual DbSet<TicketMessage> TicketMessages { get; set; } = null!;

    public virtual DbSet<PriceListImport> PriceListImports { get; set; } = null!;

    public virtual DbSet<PriceListImportRow> PriceListImportRows { get; set; } = null!;

    public virtual DbSet<BestOffer> BestOffers { get; set; } = null!;

    public virtual DbSet<ManagerAccess> ManagerAccesses { get; set; } = null!;

    public virtual DbSet<ExtraClient> ExtraClients { get; set; } = null!;

    private static void ConfigureLateSchema(ModelBuilder modelBuilder)
    {
        ConfigureManagerReach(modelBuilder);
        ConfigureColumnsTheScaffoldMissed(modelBuilder);
        ConfigureCatalogueExtras(modelBuilder);
        ConfigureSupport(modelBuilder);
        ConfigureImports(modelBuilder);
        ConfigureBestOffer(modelBuilder);
        ConfigureMissingDefaults(modelBuilder);
    }

    /// <summary>
    /// How far a salesperson's reach goes, and the accounts granted one at a
    /// time.
    /// </summary>
    /// <remarks>
    /// The two tables <c>ManagerReach</c> has been waiting for. Every guard
    /// and every scoped query already handles Selected and All; only Own was
    /// reachable, because there was nowhere to record the other two.
    ///
    /// ONE CASCADE PER TABLE, AND THE REST NO ACTION
    /// ---------------------------------------------
    /// PostgreSQL cascades all five of these from Client. SQL Server allows
    /// exactly one path per pair of tables and refuses the second — asked
    /// directly rather than assumed:
    ///
    ///   first  CASCADE                      accepted
    ///   second CASCADE to the same table    refused
    ///   second as NO ACTION                 accepted
    ///
    /// So the cascade goes to the one that matters — deleting a salesperson
    /// takes their reach and their grants with them — and the rest refuse the
    /// delete instead. The difference is real and is recorded as an allowance
    /// in tools/schema-audit.mjs rather than left to be rediscovered: on
    /// PostgreSQL, deleting a customer who appears in somebody's granted list
    /// tidies the grant away; here it is refused. Nothing in this application
    /// deletes a Client.
    /// </remarks>
    private static void ConfigureManagerReach(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagerAccess>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ManagerAccess_pkey");
            entity.ToTable("ManagerAccess");

            // One row per salesperson: two would be two answers to how far
            // their reach goes, and nothing could choose between them.
            entity.HasIndex(e => e.ManagerId, "ManagerAccess_managerId_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ManagerId).HasColumnName("managerId");
            entity.Property(e => e.Reach).HasDefaultValue("own").HasColumnName("reach");
            entity.Property(e => e.GrantedById).HasColumnName("grantedById");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("updatedAt");

            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.ManagerId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("ManagerAccess_managerId_fkey");

            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.GrantedById)
                .OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("ManagerAccess_grantedById_fkey");

            // A reach nothing recognises would be read as whichever degree the
            // last branch happens to be, and the safe reading of an unknown
            // permission is not something to leave to a switch expression.
            entity.ToTable(t => t.HasCheckConstraint(
                "ManagerAccess_reach_known", "\"reach\" IN ('own', 'selected', 'all')"));
        });

        modelBuilder.Entity<ExtraClient>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ExtraClient_pkey");
            entity.ToTable("ExtraClient");

            // Granting the same account twice is granting it once.
            entity.HasIndex(e => new { e.ManagerId, e.ClientId }, "ExtraClient_managerId_clientId_key")
                .IsUnique();
            entity.HasIndex(e => e.ManagerId, "ExtraClient_managerId_idx");
            entity.HasIndex(e => e.ClientId, "ExtraClient_clientId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ManagerId).HasColumnName("managerId");
            entity.Property(e => e.ClientId).HasColumnName("clientId");
            entity.Property(e => e.GrantedById).HasColumnName("grantedById");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");

            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.ManagerId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("ExtraClient_managerId_fkey");

            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.ClientId)
                .OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("ExtraClient_clientId_fkey");

            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.GrantedById)
                .OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("ExtraClient_grantedById_fkey");

            // Nobody is granted themselves. Their own accounts are the ones
            // naming them in salesManagerId, which every reach includes —
            // a row here saying otherwise grants nothing and reads as though
            // it did.
            entity.ToTable(t => t.HasCheckConstraint(
                "ExtraClient_not_self", "\"managerId\" <> \"clientId\""));
        });
    }

    /// <summary>
    /// Columns added to tables that were already in the model.
    /// </summary>
    /// <remarks>
    /// Only the ones needing something said about them — a column name that is
    /// not the property name lowercased, a default the database applies, or a
    /// precision. The rest are matched by the naming convention below.
    /// </remarks>
    private static void ConfigureColumnsTheScaffoldMissed(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.Property(e => e.GoodsCategoryId).HasColumnName("goodsCategoryId");
            entity.Property(e => e.PackagingUnit).HasDefaultValue("piece").HasColumnName("packagingUnit");
            entity.Property(e => e.PartType).HasDefaultValue("aftermarket").HasColumnName("partType");
            entity.Property(e => e.QuantityPerPackage).HasDefaultValue(1).HasColumnName("quantityPerPackage");
            entity.Property(e => e.WeightGrams).HasColumnName("weightGrams");

            entity.HasIndex(e => e.PartType, "Product_partType_idx");
            entity.HasIndex(e => e.GoodsCategoryId, "Product_goodsCategoryId_idx");

            // The category may go without taking the parts with it: a part
            // priced by a category that is deleted falls back to the account's
            // own default, which is what a null here means.
            entity.HasOne(d => d.GoodsCategory).WithMany(p => p.Products)
                .HasForeignKey(d => d.GoodsCategoryId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Product_goodsCategoryId_fkey");
        });

        modelBuilder.Entity<MarkupRule>(entity =>
        {
            entity.Property(e => e.GoodsCategoryId).HasColumnName("goodsCategoryId");
            // Recomputed on every write from how many filters the rule carries;
            // it decides which of two matching rules wins, so it is never set
            // from a request body.
            entity.Property(e => e.Specificity).HasDefaultValue(0).HasColumnName("specificity");
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.Property(e => e.Carrier).HasColumnName("carrier");
            entity.Property(e => e.StatusChangedAt).HasPrecision(3).HasColumnName("statusChangedAt");
            entity.Property(e => e.StatusChangedById).HasColumnName("statusChangedById");
            entity.Property(e => e.StatusReason).HasColumnName("statusReason");
            entity.Property(e => e.TrackingNumber).HasColumnName("trackingNumber");
            // True when every line on the order has a weight. A total that is
            // missing one part's weight is not a total, and the flag is what
            // stops it being read as one.
            entity.Property(e => e.WeightComplete).HasDefaultValue(true).HasColumnName("weightComplete");
            entity.Property(e => e.WeightGrams).HasDefaultValue(0).HasColumnName("weightGrams");

            // The admin desk's list: orders of one status, newest first. It is
            // the query that runs every time somebody opens the desk, and the
            // only one on this table that reads a range rather than a row.
            entity.HasIndex(e => new { e.Status, e.CreatedAt }, "Order_status_createdAt_idx");

            // Who last moved the order, which is a SECOND relation to Client —
            // the first being whose order it is. Declared without a navigation
            // because nothing reads it as one: the name is copied onto the row
            // beside the id, so the desk can say who did it without a join.
            //
            // SET NULL, which is what PostgreSQL has. It was NO ACTION here
            // first, on the assumption that SQL Server would refuse a second
            // path between two tables it already joins — it refuses a second
            // CASCADE path, and the first path is Restrict. The engine was
            // asked and accepted it.
            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.StatusChangedById)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Order_statusChangedById_fkey");
        });

        modelBuilder.Entity<PriceList>(entity =>
            entity.Property(e => e.MarkupPercent).HasColumnName("markupPercent"));

        modelBuilder.Entity<PriceListItem>(entity =>
        {
            entity.Property(e => e.MarkupPercent).HasColumnName("markupPercent");
            // The number as the supplier's own file spelled it, kept so a row
            // can be traced back to the line it came from.
            entity.Property(e => e.SourcePartNumber).HasColumnName("sourcePartNumber");
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(e => e.MarkupPercent).HasColumnName("markupPercent");
            entity.Property(e => e.MinOrderAmount).HasDefaultValue(0d).HasColumnName("minOrderAmount");
            // Highest first, and the first thing BestOffer orders by: a
            // preferred supplier wins even when somebody else is a penny
            // cheaper.
            entity.Property(e => e.Priority).HasDefaultValue(0).HasColumnName("priority");
        });

        modelBuilder.Entity<VehicleModel>(entity =>
            entity.Property(e => e.Series).HasColumnName("series"));

        modelBuilder.Entity<VehicleVariant>(entity =>
        {
            entity.Property(e => e.BodyType).HasColumnName("bodyType");
            entity.Property(e => e.Region).HasColumnName("region");
            entity.Property(e => e.SteeringSide).HasColumnName("steeringSide");
            entity.Property(e => e.Transmission).HasColumnName("transmission");
        });
    }

    private static void ConfigureCatalogueExtras(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductSpec>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ProductSpec_pkey");
            entity.ToTable("ProductSpec");

            // The page reads every spec for one part in display order, and that
            // is the only way anything reads this table.
            entity.HasIndex(e => new { e.ProductId, e.SortOrder }, "ProductSpec_productId_sortOrder_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.Label).HasColumnName("label");
            entity.Property(e => e.Value).HasColumnName("value");
            entity.Property(e => e.Unit).HasColumnName("unit");
            entity.Property(e => e.SortOrder).HasDefaultValue(0).HasColumnName("sortOrder");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");

            entity.HasOne(d => d.Product).WithMany()
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("ProductSpec_productId_fkey");
        });

        modelBuilder.Entity<ProductBarcode>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ProductBarcode_pkey");
            entity.ToTable("ProductBarcode");

            // A scan asks "which part is this", so the code is the lookup and
            // it has to be unique across the catalogue.
            entity.HasIndex(e => e.Code, "ProductBarcode_code_key").IsUnique();
            entity.HasIndex(e => new { e.ProductId, e.SortOrder }, "ProductBarcode_productId_sortOrder_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.Kind).HasDefaultValue("other").HasColumnName("kind");
            entity.Property(e => e.SortOrder).HasDefaultValue(0).HasColumnName("sortOrder");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");

            entity.HasOne(d => d.Product).WithMany()
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("ProductBarcode_productId_fkey");
        });

        modelBuilder.Entity<GoodsCategory>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("GoodsCategory_pkey");
            entity.ToTable("GoodsCategory");

            entity.HasIndex(e => e.Slug, "GoodsCategory_slug_key").IsUnique();
            // Missed when this table was written out by hand — slug got its
            // unique and name did not. Two categories called the same thing is
            // not a crash; it is an admin list with the same row in it twice
            // and a markup that depends on which one a part happened to be
            // filed under.
            entity.HasIndex(e => e.Name, "GoodsCategory_name_key").IsUnique();

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Slug).HasColumnName("slug");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.MarkupType).HasColumnName("markupType");
            entity.Property(e => e.MarkupValue).HasColumnName("markupValue");
            entity.Property(e => e.MarkupMinAmount).HasColumnName("markupMinAmount");
            entity.Property(e => e.SortOrder).HasDefaultValue(0).HasColumnName("sortOrder");
            entity.Property(e => e.Active).HasDefaultValue(true).HasColumnName("active");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");

            // The four constraints the database already carries, said in a way
            // SQL Server accepts.
            //
            // PostgreSQL writes the first and third as an equality between two
            // comparisons — `("markupType" IS NULL) = ("markupValue" IS NULL)`.
            // That reads naturally there because a comparison yields a value of
            // type boolean and booleans can be compared. SQL Server has no
            // boolean type at all: a comparison is a condition, not a value,
            // and there is nothing to put on either side of that `=`. Both
            // become the disjunction they mean.
            entity.ToTable(t =>
            {
                // Both null or both set. Half a markup is not a smaller markup,
                // it is an incomplete one, and the row carrying it would price
                // something by accident.
                t.HasCheckConstraint(
                    "GoodsCategory_markup_complete",
                    """
                    ("markupType" IS NULL AND "markupValue" IS NULL)
                    OR ("markupType" IS NOT NULL AND "markupValue" IS NOT NULL)
                    """);

                // The same vocabulary the markup rules use, because a
                // category's markup and a rule's markup are the same arithmetic
                // applied at different points.
                t.HasCheckConstraint(
                    "GoodsCategory_markupType_known",
                    """
                    "markupType" IS NULL
                    OR "markupType" IN ('PERCENT', 'AMOUNT', 'FIXED', 'PERCENT_MIN')
                    """);

                // A floor belongs to exactly one kind of markup. Against a
                // fixed amount it is not a smaller floor, it is a contradiction.
                t.HasCheckConstraint(
                    "GoodsCategory_floor_matches_type",
                    """
                    ("markupMinAmount" IS NULL
                     AND ("markupType" IS NULL OR "markupType" <> 'PERCENT_MIN'))
                    OR ("markupMinAmount" IS NOT NULL AND "markupType" = 'PERCENT_MIN')
                    """);

                // A floor below nothing is a floor that never applies.
                t.HasCheckConstraint(
                    "GoodsCategory_floor_positive",
                    """
                    "markupMinAmount" IS NULL OR "markupMinAmount" >= 0
                    """);
            });
        });

        modelBuilder.Entity<VinLookup>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("VinLookup_pkey");
            entity.ToTable("VinLookup");

            // One row per pattern is the whole design, and this is what makes
            // it true rather than intended.
            entity.HasIndex(e => e.Pattern, "VinLookup_pattern_key").IsUnique();
            entity.HasIndex(e => e.Wmi, "VinLookup_wmi_idx");
            // The other half of the same pair, missed here. It answers "what
            // has been decoded lately", which is the report this table exists
            // to feed.
            entity.HasIndex(e => e.DecodedAt, "VinLookup_decodedAt_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Pattern).HasColumnName("pattern");
            entity.Property(e => e.Wmi).HasColumnName("wmi");
            entity.Property(e => e.ModelYear).HasColumnName("modelYear");
            entity.Property(e => e.MakeName).HasColumnName("makeName");
            entity.Property(e => e.CandidateCount).HasDefaultValue(0).HasColumnName("candidateCount");
            entity.Property(e => e.Lookups).HasDefaultValue(1).HasColumnName("lookups");
            entity.Property(e => e.FirstSeenAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("firstSeenAt");
            entity.Property(e => e.LastSeenAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("lastSeenAt");
            entity.Property(e => e.DecodedAt).HasPrecision(3).HasColumnName("decodedAt");
            entity.Property(e => e.Decoded).HasColumnName("decoded");
            entity.Property(e => e.DecodedModel).HasColumnName("decodedModel");
            entity.Property(e => e.DecodedTrim).HasColumnName("decodedTrim");
            entity.Property(e => e.DecodedEngine).HasColumnName("decodedEngine");
            entity.Property(e => e.DecoderStatus).HasColumnName("decoderStatus");
        });

        modelBuilder.Entity<SearchMiss>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("SearchMiss_pkey");
            entity.ToTable("SearchMiss");

            // Same term, narrowed and not narrowed, are two findings and two
            // rows — see the property.
            entity.HasIndex(e => new { e.Term, e.Narrowed }, "SearchMiss_term_narrowed_key").IsUnique();
            entity.HasIndex(e => e.LastSeenAt, "SearchMiss_lastSeenAt_idx");
            // Missed beside it. The admin report reads this table twice —
            // what was searched for most, and what was searched for last —
            // and only one of the two had an index.
            entity.HasIndex(e => e.Searches, "SearchMiss_searches_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Term).HasColumnName("term");
            entity.Property(e => e.Narrowed).HasDefaultValue(false).HasColumnName("narrowed");
            entity.Property(e => e.Searches).HasDefaultValue(1).HasColumnName("searches");
            entity.Property(e => e.FirstSeenAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("firstSeenAt");
            entity.Property(e => e.LastSeenAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("lastSeenAt");
        });

        modelBuilder.Entity<SupplierOffer>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("SupplierOffer_pkey");
            entity.ToTable("SupplierOffer");

            // One offer per supplier per part. A second is an edit, not a
            // second opinion.
            entity.HasIndex(e => new { e.ProductId, e.SupplierId }, "SupplierOffer_productId_supplierId_key")
                .IsUnique();
            entity.HasIndex(e => e.SupplierId, "SupplierOffer_supplierId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.SupplierId).HasColumnName("supplierId");
            entity.Property(e => e.PurchasePrice).HasColumnName("purchasePrice");
            entity.Property(e => e.StockDays).HasColumnName("stockDays");
            entity.Property(e => e.SupplierPartNumber).HasColumnName("supplierPartNumber");
            entity.Property(e => e.Active).HasDefaultValue(true).HasColumnName("active");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("updatedAt");

            entity.HasOne(d => d.Product).WithMany()
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("SupplierOffer_productId_fkey");

            entity.HasOne(d => d.Supplier).WithMany()
                .HasForeignKey(d => d.SupplierId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("SupplierOffer_supplierId_fkey");
        });
    }

    private static void ConfigureSupport(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Ticket_pkey");
            entity.ToTable("Ticket");

            entity.HasIndex(e => e.Reference, "Ticket_reference_key").IsUnique();
            entity.HasIndex(e => e.ClientId, "Ticket_clientId_idx");
            // The queue: open ones, oldest waiting first.
            entity.HasIndex(e => new { e.Status, e.LastMessageAt }, "Ticket_status_lastMessageAt_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Reference).HasColumnName("reference");
            entity.Property(e => e.ClientId).HasColumnName("clientId");
            entity.Property(e => e.OrderId).HasColumnName("orderId");
            entity.Property(e => e.Subject).HasColumnName("subject");
            entity.Property(e => e.Status).HasDefaultValue("open").HasColumnName("status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");
            entity.Property(e => e.LastMessageAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("lastMessageAt");

            entity.HasOne(d => d.Client).WithMany()
                .HasForeignKey(d => d.ClientId)
                .OnDelete(DeleteBehavior.NoAction)
                .HasConstraintName("Ticket_clientId_fkey");

            // The order may go; the conversation about it stays, because it is
            // also the record of what was said.
            entity.HasOne(d => d.Order).WithMany()
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Ticket_orderId_fkey");
        });

        modelBuilder.Entity<TicketMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("TicketMessage_pkey");
            entity.ToTable("TicketMessage");

            entity.HasIndex(e => new { e.TicketId, e.CreatedAt }, "TicketMessage_ticketId_createdAt_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TicketId).HasColumnName("ticketId");
            entity.Property(e => e.AuthorId).HasColumnName("authorId");
            // Who wrote it — a second relation to Client, the first being
            // whose ticket it is. No navigation: the name is copied onto the
            // row, so a thread renders without a join. SET NULL, as PostgreSQL
            // has: the message stays and stops naming an account that is gone.
            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.AuthorId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("TicketMessage_authorId_fkey");
            entity.Property(e => e.AuthorName).HasColumnName("authorName");
            entity.Property(e => e.FromStaff).HasColumnName("fromStaff");
            entity.Property(e => e.Internal).HasDefaultValue(false).HasColumnName("internal");
            entity.Property(e => e.Body).HasMaxLength(LongText).HasColumnName("body");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");

            entity.HasOne(d => d.Ticket).WithMany(p => p.Messages)
                .HasForeignKey(d => d.TicketId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("TicketMessage_ticketId_fkey");
        });
    }

    private static void ConfigureImports(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceListImport>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PriceListImport_pkey");
            entity.ToTable("PriceListImport");

            entity.HasIndex(e => e.CreatedAt, "PriceListImport_createdAt_idx");
            entity.HasIndex(e => e.PriceListId, "PriceListImport_priceListId_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.PriceListId).HasColumnName("priceListId");
            entity.Property(e => e.ListName).HasColumnName("listName");
            entity.Property(e => e.SourceName).HasColumnName("sourceName");
            entity.Property(e => e.UploadedById).HasColumnName("uploadedById");
            // Who uploaded the file. SET NULL, as PostgreSQL has — an import
            // is a record of something that happened and should outlive the
            // account that did it, which is what SET NULL does and what NO
            // ACTION would have prevented by refusing the delete outright.
            entity.HasOne<Client>().WithMany()
                .HasForeignKey(e => e.UploadedById)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("PriceListImport_uploadedById_fkey");
            entity.Property(e => e.UploadedByName).HasColumnName("uploadedByName");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.RowsSent).HasDefaultValue(0).HasColumnName("rowsSent");
            entity.Property(e => e.Accepted).HasDefaultValue(0).HasColumnName("accepted");
            entity.Property(e => e.Rejected).HasDefaultValue(0).HasColumnName("rejected");
            entity.Property(e => e.RejectedStored).HasDefaultValue(0).HasColumnName("rejectedStored");
            entity.Property(e => e.Error).HasMaxLength(LongText).HasColumnName("error");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("SYSUTCDATETIME()").HasPrecision(3).HasColumnName("createdAt");

            // The log outlives the list. Deleting a price list must not delete
            // the record of what loading it did.
            entity.HasOne(d => d.PriceList).WithMany()
                .HasForeignKey(d => d.PriceListId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("PriceListImport_priceListId_fkey");
        });

        modelBuilder.Entity<PriceListImportRow>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PriceListImportRow_pkey");
            entity.ToTable("PriceListImportRow");

            entity.HasIndex(e => new { e.ImportId, e.Line }, "PriceListImportRow_importId_line_idx");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ImportId).HasColumnName("importId");
            entity.Property(e => e.Line).HasColumnName("line");
            entity.Property(e => e.PartNumber).HasColumnName("partNumber");
            entity.Property(e => e.Price).HasColumnName("price");
            entity.Property(e => e.Currency).HasColumnName("currency");
            entity.Property(e => e.Reason).HasColumnName("reason");

            entity.HasOne(d => d.Import).WithMany(p => p.Rows)
                .HasForeignKey(d => d.ImportId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("PriceListImportRow_importId_fkey");
        });
    }

    /// <summary>
    /// The one view, and the one place <c>DISTINCT ON</c> had to be rewritten.
    /// </summary>
    /// <remarks>
    /// PostgreSQL's <c>DISTINCT ON (o."productId") … ORDER BY o."productId", …</c>
    /// keeps the first row per part under the given order. SQL Server has no
    /// such thing, and the standard way to say it is a window function: number
    /// the rows within each part by the same order, keep number one.
    ///
    /// The two are equivalent here because the order is total — supplier
    /// priority, then price, then supplier code, and the code is unique. That
    /// matters more than it looks: <c>DISTINCT ON</c> under a partial order
    /// picks an arbitrary row among ties, and so does <c>ROW_NUMBER</c>, but
    /// they need not pick the same one. With a unique final tie-break there
    /// are no ties left to disagree about, which is why the tie-break is in
    /// the ordering rather than being left to the engine.
    ///
    /// Created as raw SQL in the migration rather than by EF, which does not
    /// generate views. Keyless here so nothing tries to track or update it.
    /// </remarks>
    private static void ConfigureBestOffer(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BestOffer>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("BestOffer");

            entity.Property(e => e.ProductId).HasColumnName("productId");
            entity.Property(e => e.SupplierId).HasColumnName("supplierId");
            entity.Property(e => e.PurchasePrice).HasColumnName("purchasePrice");
            entity.Property(e => e.StockDays).HasColumnName("stockDays");
            entity.Property(e => e.SupplierCode).HasColumnName("supplierCode");
            entity.Property(e => e.SupplierName).HasColumnName("supplierName");
        });

    /// <summary>The SQL Server form of the view, applied by the BestOfferView migration.</summary>
    public const string BestOfferSql = """
        CREATE VIEW "BestOffer" AS
        SELECT "productId", "supplierId", "purchasePrice", "stockDays",
               "supplierCode", "supplierName"
        FROM (
          SELECT o."productId", o."supplierId", o."purchasePrice", o."stockDays",
                 s."code" AS "supplierCode", s."name" AS "supplierName",
                 ROW_NUMBER() OVER (
                   PARTITION BY o."productId"
                   ORDER BY s."priority" DESC, o."purchasePrice" ASC, s."code" ASC
                 ) AS "rank"
          FROM "SupplierOffer" o
          JOIN "Supplier" s ON s."id" = o."supplierId"
          WHERE o."active" = 1 AND s."active" = 1
        ) ranked
        WHERE "rank" = 1
        """;
}
