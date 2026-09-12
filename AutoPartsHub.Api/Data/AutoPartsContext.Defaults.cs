using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Data;

/// <summary>
/// The column defaults the scaffold did not bring across.
/// </summary>
/// <remarks>
/// The same fault as <c>AutoPartsContext.LateSchema.cs</c>, one layer down.
/// The model was reverse-engineered from PostgreSQL, and scaffolding recorded
/// each column's type and nullability but not, for these fourteen, the default
/// behind it.
///
/// That was invisible for as long as the schema was being read INTO the model:
/// the defaults were really there, and every INSERT relied on them without
/// anybody noticing. It stopped being invisible the moment a schema was
/// GENERATED from the model instead — a NOT NULL column whose default did not
/// come across refuses every INSERT that does not name it, with error 515 and
/// the column's name.
///
/// All fourteen are NOT NULL. There is no gentle version of this failure.
///
/// HOW IT WAS FOUND
/// ----------------
/// By registering an account. <c>POST /api/auth/register</c> answered 500
/// because it inserts the columns it cares about and leaves
/// <c>discountPercent</c> to the database, which had nothing to give. The
/// parse check could not have found it — the statement is valid, and the
/// column it does not mention is exactly the point. Nor could the seeded
/// tests: they write rows through fixtures that name everything.
///
/// <c>tools/default-audit.mjs</c> found the other thirteen by comparing
/// Prisma's schema — which IS the PostgreSQL one — against SQL Server's
/// catalogue, so the rest were found by reading rather than by a customer.
/// Run it after any schema change.
/// </remarks>
public partial class AutoPartsContext
{
    /// <summary>One column, and what the database should put in it.</summary>
    public record ColumnDefault(string Table, string Column, string Sql);

    /// <summary>
    /// The fourteen, with the expression PostgreSQL uses for each.
    /// </summary>
    /// <remarks>
    /// One list, and the migration is generated from the model it configures
    /// rather than written beside it. Two lists is how a model and a schema
    /// come to disagree, which is the fault being fixed here rather than a
    /// shape to repeat.
    ///
    /// Every one of them is zero or false. That is not a coincidence worth
    /// relying on — it is what "a counter nobody has set" and "a flag nobody
    /// has ticked" mean — but it is worth noticing, because it says these are
    /// starting values rather than business decisions, and the value is not
    /// the interesting part. The interesting part is that the column cannot be
    /// left out.
    /// </remarks>
    public static readonly ColumnDefault[] DefaultsTheScaffoldMissed =
    [
        new("VehicleSystem", "order", "0"),
        new("Manufacturer", "isOEM", "0"),
        new("PriceList", "active", "0"),
        new("ProductImage", "sortOrder", "0"),
        new("Interchange", "exactMatch", "0"),
        new("Interchange", "isOEM", "0"),
        new("ClientCategory", "minOrderAmount", "0"),
        new("ClientCategory", "requiresApproval", "0"),
        new("Currency", "isBase", "0"),
        new("Client", "discountPercent", "0"),
        new("Warehouse", "priority", "0"),
        new("StockLevel", "quantity", "0"),
        new("StockLevel", "reserved", "0"),
        new("MarkupRule", "priority", "0"),
    ];

    /// <summary>
    /// Puts them on the model, so a schema generated from it carries them.
    /// </summary>
    /// <remarks>
    /// By column name rather than by property, because the list above is
    /// written in the database's vocabulary — it was produced by comparing two
    /// catalogues, and translating it into fourteen property names would make
    /// the list and the audit that found it stop matching.
    ///
    /// A column named here that the model does not have is a mistake worth
    /// hearing about: it means the list has drifted from the schema, which is
    /// the one failure this whole file exists to prevent. So it throws rather
    /// than skipping.
    /// </remarks>
    private static void ConfigureMissingDefaults(ModelBuilder modelBuilder)
    {
        foreach (var gap in DefaultsTheScaffoldMissed)
        {
            var entity = modelBuilder.Model.GetEntityTypes()
                .FirstOrDefault(e => e.GetTableName() == gap.Table)
                ?? throw new InvalidOperationException(
                    $"No entity is mapped to \"{gap.Table}\", so its default for "
                    + $"\"{gap.Column}\" cannot be applied. See AutoPartsContext.Defaults.cs.");

            var property = entity.GetProperties()
                .FirstOrDefault(p => p.GetColumnName() == gap.Column)
                ?? throw new InvalidOperationException(
                    $"\"{gap.Table}\".\"{gap.Column}\" is not on the model, so its default "
                    + "cannot be applied. See AutoPartsContext.Defaults.cs.");

            property.SetDefaultValueSql(gap.Sql);
        }
    }
}
