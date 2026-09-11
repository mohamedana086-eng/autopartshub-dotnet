using AutoPartsHub.Api.Data;
using AutoPartsHub.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Catalogue;

/// <summary>
/// Reading a part's barcodes.
/// </summary>
/// <remarks>
/// Separate from <see cref="Barcodes"/>, which is the pure vocabulary — check
/// digits and normalisation, shared with anything that validates one. This is
/// the half that talks to the database, and keeping them apart is what lets
/// the rules be unit-tested without one.
///
/// Raw SQL rather than a scaffolded entity, like the specifications beside it:
/// nothing here writes a barcode, so there is nothing for change tracking to
/// do.
/// </remarks>
public sealed class BarcodeQueries(AutoPartsContext db)
{
    /// <summary>Barcodes for a batch of parts, grouped by part and in display order.</summary>
    public async Task<Dictionary<string, List<Barcode>>> ForAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var grouped = new Dictionary<string, List<Barcode>>();
        if (ids.Count == 0) return grouped;

        var array = ids.ToArray();

        var rows = await db.Database.SqlQuery<BarcodeRow>($"""
            SELECT "productId" AS "ProductId", "code" AS "Code", "kind" AS "Kind"
            FROM "ProductBarcode"
            WHERE "productId" = ANY({array}::text[])
            -- The code breaks a tie on sortOrder, so two codes given the same
            -- position do not swap places between requests. The code is
            -- unique, so this is a total order rather than merely a tidier one.
            ORDER BY "productId" ASC, "sortOrder" ASC, "code" ASC
            """).ToListAsync(ct);

        foreach (var row in rows)
        {
            if (!grouped.TryGetValue(row.ProductId, out var list))
            {
                list = [];
                grouped[row.ProductId] = list;
            }
            list.Add(new Barcode(row.Code, row.Kind));
        }

        return grouped;
    }

    /// <summary>
    /// The part a scanned code belongs to, or null.
    /// </summary>
    /// <remarks>
    /// The whole reason the code column is unique. A scan that could return
    /// two parts is a question, not an answer, and this signature is where
    /// that decision shows: it returns one part or none, and cannot be made to
    /// return a list later without the caller noticing.
    /// </remarks>
    public async Task<string?> ProductIdByCodeAsync(string code, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<string>($"""
            SELECT TOP 1 "productId" AS "Value" FROM "ProductBarcode" WHERE "code" = {code}
            """).ToListAsync(ct);
        return rows.FirstOrDefault();
    }
}

/// <summary>One barcode, as it goes out on the wire.</summary>
public record Barcode(string Code, string Kind);

/// <summary>The same, still carrying the part it belongs to, for grouping.</summary>
public record BarcodeRow(string ProductId, string Code, string Kind);
