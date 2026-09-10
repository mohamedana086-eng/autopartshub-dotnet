using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Data;

/// <summary>
/// The conventions the scaffolded half of this context does not carry.
/// </summary>
/// <remarks>
/// The model was generated from the PostgreSQL database, where every string
/// column is <c>text</c> and no length is recorded anywhere. Read back into
/// EF that becomes "a string of unknown length", and on SQL Server a string of
/// unknown length is <c>nvarchar(max)</c> — forty-three of them, on the first
/// migration.
///
/// WHY THAT MATTERS ON ONE ENGINE AND NOT THE OTHER
/// ------------------------------------------------
/// PostgreSQL's <c>text</c> is not a compromise: it is stored the same way as
/// a bounded string and indexes the same way. SQL Server's <c>nvarchar(max)</c>
/// is a different thing wearing a similar name. It cannot be an index key at
/// all, it is stored off-row once it grows, and a column that cannot be an
/// index key is a column every lookup scans for.
///
/// Half of the backlog's performance work — the article-number seeks in EPIC 4,
/// the covering indexes in EPIC 3 — is about looking things up by exactly the
/// columns that arrived here as <c>nvarchar(max)</c>: part numbers,
/// cross-reference numbers, supplier codes, statuses. None of those indexes can
/// be created against the schema as generated.
///
/// So: everything is bounded, and the few genuinely long ones say so
/// explicitly below. 400 characters is 800 bytes, which fits under SQL Server's
/// 900-byte index key limit with room to spare — the number is chosen so that
/// any column can be indexed without anybody having to check first.
///
/// This lives in the partial hook rather than in the generated file so that
/// re-scaffolding the model from a database does not quietly delete it.
/// </remarks>
public partial class AutoPartsContext
{
    /// <summary>Long enough for a name, short enough to index.</summary>
    private const int DefaultText = 400;

    /// <summary>Free text a person typed. Never a key, never indexed.</summary>
    private const int LongText = 4000;

    /// <summary>
    /// What a browser will actually carry.
    /// </summary>
    /// <remarks>
    /// 2048 is not a standard, it is the shortest limit among the browsers that
    /// have one, and a URL longer than it does not work regardless of what the
    /// column would hold.
    /// </remarks>
    private const int UrlText = 2048;

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Every string, unless something below says otherwise. A default that
        // has to be opted into is a default that is missing from the column
        // somebody adds next month.
        configuration.Properties<string>().HaveMaxLength(DefaultText);

        // Money and percentages arrived as `double precision` and are `double`
        // in the model, so they map to `float` here. That is wrong for money
        // and it is not fixed in this file: the pricing engine computes in
        // double throughout, and moving it to decimal is a change to the
        // arithmetic and its rounding — T-036 — rather than to a column type.
        // Doing half of it would be worse than neither half, because a decimal
        // column fed by double arithmetic still carries the error and now hides
        // it behind an exact type.
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // The handful that are genuinely long. Everything not named here is
        // a name, a code, a status or a city, and fits in 400 with room.
        modelBuilder.Entity<Entities.Fitment>().Property(e => e.Note).HasMaxLength(LongText);
        modelBuilder.Entity<Entities.Notification>().Property(e => e.Body).HasMaxLength(LongText);
        modelBuilder.Entity<Entities.PriceList>().Property(e => e.Description).HasMaxLength(LongText);
        modelBuilder.Entity<Entities.Product>().Property(e => e.Description).HasMaxLength(LongText);
        modelBuilder.Entity<Entities.Supplier>().Property(e => e.Description).HasMaxLength(LongText);

        modelBuilder.Entity<Entities.Notification>().Property(e => e.Link).HasMaxLength(UrlText);
        modelBuilder.Entity<Entities.ProductImage>().Property(e => e.Url).HasMaxLength(UrlText);
    }
}
