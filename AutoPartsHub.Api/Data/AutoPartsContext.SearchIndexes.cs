namespace AutoPartsHub.Api.Data;

/// <summary>
/// What the near-miss search seeks on.
/// </summary>
/// <remarks>
/// PostgreSQL answered "nothing matched — did you mean?" with pg_trgm: one
/// <c>word_similarity</c> per row, over the whole product table, on every
/// search that came up empty. It worked, and it was a scan wearing a score.
/// SQL Server has no equivalent, which is the reason T-067 exists, and the
/// replacement it specifies is two index seeks — a prefix on part numbers and
/// full text on names.
///
/// Neither of those could seek against the schema as it stood. Part numbers
/// are stored as they are printed — <c>0 986 424 815</c>, <c>W712/30</c> —
/// and every query that looked one up normalised it with
/// <c>regexp_replace</c> per row, which cannot use the index on the column it
/// is reading. Two separate places in this application said so in a comment
/// and left it: "if it grows this wants a normalised column with an index on
/// it". This is that column.
///
/// UNMAPPED ON PURPOSE
/// -------------------
/// These are not in the model. Nothing reads them through EF — they exist so
/// that a <c>LIKE 'ABC12%'</c> can seek — and a mapped computed column would
/// be selected into every Product query that never asked for it, as well as
/// putting the schema in the snapshot, where regenerating the initial
/// migration would silently take it away again. The <c>BestOffer</c> view is
/// kept out for the same reason and in the same shape: SQL beside the model,
/// applied by a migration of its own.
/// </remarks>
public partial class AutoPartsContext
{
    /// <summary>
    /// The normalised part-number columns, and the indexes that make them
    /// seekable.
    /// </summary>
    /// <remarks>
    /// <c>PERSISTED</c> rather than computed on read, because only a stored
    /// column can be indexed here — and stored means the normalisation happens
    /// once per write instead of once per row per search.
    ///
    /// It also means the database owns the rule. Two applications write to
    /// this catalogue and a third imports price lists into it; a normalised
    /// column maintained by whichever of them remembered would be wrong the
    /// first time one of them did not. <see cref="PartNumbers.Normalise"/>
    /// still exists, because the needle has to be normalised the same way
    /// before it is compared, and there is a test that the two agree.
    ///
    /// The <c>CAST</c> is not decoration: without it the expression types as
    /// the widest thing <c>regexp_replace</c> can return, and the index on it
    /// is refused for exceeding the key length.
    /// </remarks>
    public static readonly string[] SearchIndexSql =
    [
        """
        ALTER TABLE "Product" ADD "partNumberNormalised"
            AS CAST(regexp_replace(upper("partNumber"), '[^A-Z0-9]', '') AS nvarchar(400)) PERSISTED
        """,
        """
        CREATE INDEX "Product_partNumberNormalised_idx"
            ON "Product" ("partNumberNormalised") INCLUDE ("partNumber")
        """,
        """
        ALTER TABLE "Interchange" ADD "targetPartNoNormalised"
            AS CAST(regexp_replace(upper("targetPartNo"), '[^A-Z0-9]', '') AS nvarchar(400)) PERSISTED
        """,
        """
        CREATE INDEX "Interchange_targetPartNoNormalised_idx"
            ON "Interchange" ("targetPartNoNormalised") INCLUDE ("sourceId", "targetPartNo")
        """,

        // The name lane's seek when there is no full-text index to use — see
        // FullTextSearch for when that is. Product had no index on name at
        // all, because nothing ever ordered or filtered by it: the ranked
        // search matches names through the query below, and the fallback used
        // to match them with a similarity score that read every row anyway.
        """
        CREATE INDEX "Product_name_idx" ON "Product" ("name") INCLUDE ("partNumber")
        """,
    ];

    /// <summary>Undoes <see cref="SearchIndexSql"/>, in the reverse order.</summary>
    public static readonly string[] DropSearchIndexSql =
    [
        """DROP INDEX "Product_name_idx" ON "Product" """,
        """DROP INDEX "Interchange_targetPartNoNormalised_idx" ON "Interchange" """,
        """ALTER TABLE "Interchange" DROP COLUMN "targetPartNoNormalised" """,
        """DROP INDEX "Product_partNumberNormalised_idx" ON "Product" """,
        """ALTER TABLE "Product" DROP COLUMN "partNumberNormalised" """,
    ];

    /// <summary>The full-text catalogue the name lane uses when it exists.</summary>
    /// <remarks>
    /// Guarded by <c>IsFullTextInstalled</c> rather than assumed, because it
    /// is a separate feature of the engine and not every edition has it: this
    /// migration has only ever been applied to an instance without it, where
    /// the guard is the difference between a deployment that degrades and one
    /// that will not start. <see cref="Catalogue.FullTextSearch"/> asks the
    /// same question at query time and picks the lane to match, so a
    /// deployment that gains or loses full text stays correct either way.
    ///
    /// No language is named, so the server's default full-text language does
    /// the word breaking. That is the knob to turn if the catalogue is not in
    /// it — naming one here would be a guess about a deployment that has not
    /// been chosen yet (BLK-003).
    ///
    /// <c>EXEC</c> because CREATE FULLTEXT CATALOG has to be the only
    /// statement in its batch, which it cannot be inside an IF.
    /// </remarks>
    public const string FullTextSql = """
        IF SERVERPROPERTY('IsFullTextInstalled') = 1
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'AutoPartsSearch')
                EXEC(N'CREATE FULLTEXT CATALOG [AutoPartsSearch]');

            IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('Product'))
                EXEC(N'CREATE FULLTEXT INDEX ON "Product" ("name")
                       KEY INDEX "Product_pkey" ON [AutoPartsSearch]
                       WITH CHANGE_TRACKING AUTO');
        END
        """;

    /// <summary>Undoes <see cref="FullTextSql"/>, and is a no-op where it was.</summary>
    public const string DropFullTextSql = """
        IF SERVERPROPERTY('IsFullTextInstalled') = 1
        BEGIN
            IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('Product'))
                EXEC(N'DROP FULLTEXT INDEX ON "Product"');

            IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'AutoPartsSearch')
                EXEC(N'DROP FULLTEXT CATALOG [AutoPartsSearch]');
        END
        """;
}
