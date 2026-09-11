using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Vehicles;

/// <summary>
/// Finding a car from what somebody can see, in any order.
/// </summary>
/// <remarks>
/// A customer who does not know their VIN knows other things: the shape, which
/// side the wheel is on, whether it is automatic. They may have no idea which
/// series it belongs to. A form that insists on make, then series, then model
/// makes them guess at the question they cannot answer, and a guess sends them
/// to the wrong parts.
///
/// EACH LIST LEAVES ITS OWN FILTER OUT
/// -----------------------------------
/// The rule that makes the form usable rather than a trap. If the body-type
/// list were computed with the body-type filter applied, picking "SUV" would
/// collapse that list to "SUV" alone — and a customer who picked wrong would
/// have no way to see that "Estate" was ever an option.
///
/// Done in one pass: each row carries nine booleans saying which filters it
/// satisfies, and each list reads the eight that are not its own.
///
/// A NULL DOES NOT RULE A CAR OUT
/// ------------------------------
/// A variant nobody has classified must not disappear from a search that
/// filters on a field it does not carry. Hiding the customer's actual car is
/// worse than showing one extra.
/// </remarks>
public sealed class VehicleFinder(AutoPartsContext db)
{
    public async Task<List<FinderOption>> OptionsAsync(FinderFilters f, CancellationToken ct = default)
    {
        var make = f.Make;
        var series = f.Series;
        var model = f.Model;
        var year = f.Year;
        var bodyType = f.BodyType;
        var steeringSide = f.SteeringSide;
        var transmission = f.Transmission;
        var engine = f.Engine;
        var region = f.Region;

        return await db.Database.SqlQuery<FinderOption>($"""
            -- Every year a vehicle in this catalogue could be from, as rows.
            --
            -- Numbered off a system view rather than generated: SQL Server's
            -- GENERATE_SERIES is not on every edition, and a recursive CTE
            -- would run into the hundred-level default the moment the range
            -- passed a century. sys.all_objects has thousands of rows in every
            -- database and is only being counted, not read.
            WITH years AS (
              SELECT TOP (200) 1899 + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS "year"
              FROM sys.all_objects
            ),
            candidates AS (
              SELECT vv."id" AS "variantId",
                     mk."name" AS "makeName",
                     mo."name" AS "modelName",
                     mo."series",
                     vv."yearFrom", vv."yearTo",
                     vv."bodyType", vv."steeringSide", vv."transmission", vv."region",
                     -- An engine is its code where the catalogue has one, and
                     -- the variant's own name where it does not.
                     COALESCE(vv."engineCode", vv."name") AS "engine",
                     -- The nine filters, as answers rather than as a WHERE
                     -- clause. Keeping them per row is what lets each list
                     -- below read the eight that are not its own.
                     CASE WHEN {make} IS NULL OR mk."name" = {make} THEN 1 ELSE 0 END AS "fMake",
                     CASE WHEN {series} IS NULL OR mo."series" = {series} OR mo."series" IS NULL THEN 1 ELSE 0 END AS "fSeries",
                     CASE WHEN {model} IS NULL OR mo."name" = {model} THEN 1 ELSE 0 END AS "fModel",
                     ({year} IS NULL
                       OR (vv."yearFrom" <= {year} AND COALESCE(vv."yearTo", 9999) >= {year})) AS "fYear",
                     CASE WHEN {bodyType} IS NULL OR vv."bodyType" = {bodyType} OR vv."bodyType" IS NULL THEN 1 ELSE 0 END AS "fBody",
                     CASE WHEN {steeringSide} IS NULL OR vv."steeringSide" = {steeringSide} OR vv."steeringSide" IS NULL THEN 1 ELSE 0 END AS "fSteering",
                     CASE WHEN {transmission} IS NULL OR vv."transmission" = {transmission} OR vv."transmission" IS NULL THEN 1 ELSE 0 END AS "fTransmission",
                     CASE WHEN {region} IS NULL OR vv."region" = {region} OR vv."region" IS NULL THEN 1 ELSE 0 END AS "fRegion",
                     CASE WHEN {engine} IS NULL OR COALESCE(vv."engineCode", vv."name") = {engine} THEN 1 ELSE 0 END AS "fEngine"
              FROM "VehicleVariant" vv
              JOIN "VehicleModel" mo ON mo."id" = vv."modelId"
              JOIN "VehicleMake" mk ON mk."id" = mo."makeId"
            )
            SELECT 'make' AS "Field", "makeName" AS "Value", COUNT(*) AS "Vehicles"
            FROM candidates
            WHERE "fSeries" = 1 AND "fModel" = 1 AND "fYear" = 1 AND "fBody" = 1 AND "fSteering" = 1
              AND "fTransmission" = 1 AND "fRegion" = 1 AND "fEngine" = 1
            GROUP BY "makeName"

            UNION ALL
            SELECT 'series', "series", COUNT(*) FROM candidates
            WHERE "series" IS NOT NULL
              AND "fMake" = 1 AND "fModel" = 1 AND "fYear" = 1 AND "fBody" = 1 AND "fSteering" = 1
              AND "fTransmission" = 1 AND "fRegion" = 1 AND "fEngine" = 1
            GROUP BY "series"

            UNION ALL
            SELECT 'model', "modelName", COUNT(*) FROM candidates
            WHERE "fMake" = 1 AND "fSeries" = 1 AND "fYear" = 1 AND "fBody" = 1 AND "fSteering" = 1
              AND "fTransmission" = 1 AND "fRegion" = 1 AND "fEngine" = 1
            GROUP BY "modelName"

            UNION ALL
            -- A variant covers a range of years, so the options are the years
            -- those ranges actually reach — expanded here rather than offering
            -- a bare "from" that no customer thinks in.
            SELECT 'year', y."year", COUNT(*)
            FROM candidates
            -- PostgreSQL expands the range with LATERAL generate_series. SQL
            -- Server's GENERATE_SERIES was the obvious replacement and is not
            -- available on every edition — this build refuses it at
            -- compatibility level 170 — so the years come from `years` above,
            -- which needs nothing but a table with enough rows in it.
            --
            -- A join rather than an APPLY, because the series no longer
            -- depends on the outer row: it is every year, narrowed to the
            -- variant's range by the condition.
            JOIN years y
              ON y."year" >= candidates."yearFrom"
             AND y."year" <= LEAST(COALESCE(candidates."yearTo", 9999),
                                   YEAR(SYSUTCDATETIME()) + 1)
            WHERE "fMake" = 1 AND "fSeries" = 1 AND "fModel" = 1 AND "fBody" = 1 AND "fSteering" = 1
              AND "fTransmission" = 1 AND "fRegion" = 1 AND "fEngine" = 1
            GROUP BY y."year"

            UNION ALL
            SELECT 'bodyType', "bodyType", COUNT(*) FROM candidates
            WHERE "bodyType" IS NOT NULL
              AND "fMake" = 1 AND "fSeries" = 1 AND "fModel" = 1 AND "fYear" = 1 AND "fSteering" = 1
              AND "fTransmission" = 1 AND "fRegion" = 1 AND "fEngine" = 1
            GROUP BY "bodyType"

            UNION ALL
            SELECT 'steeringSide', "steeringSide", COUNT(*) FROM candidates
            WHERE "steeringSide" IS NOT NULL
              AND "fMake" = 1 AND "fSeries" = 1 AND "fModel" = 1 AND "fYear" = 1 AND "fBody" = 1
              AND "fTransmission" = 1 AND "fRegion" = 1 AND "fEngine" = 1
            GROUP BY "steeringSide"

            UNION ALL
            SELECT 'transmission', "transmission", COUNT(*) FROM candidates
            WHERE "transmission" IS NOT NULL
              AND "fMake" = 1 AND "fSeries" = 1 AND "fModel" = 1 AND "fYear" = 1 AND "fBody" = 1
              AND "fSteering" = 1 AND "fRegion" = 1 AND "fEngine" = 1
            GROUP BY "transmission"

            UNION ALL
            SELECT 'region', "region", COUNT(*) FROM candidates
            WHERE "region" IS NOT NULL
              AND "fMake" = 1 AND "fSeries" = 1 AND "fModel" = 1 AND "fYear" = 1 AND "fBody" = 1
              AND "fSteering" = 1 AND "fTransmission" = 1 AND "fEngine" = 1
            GROUP BY "region"

            UNION ALL
            SELECT 'engine', "engine", COUNT(*) FROM candidates
            WHERE "fMake" = 1 AND "fSeries" = 1 AND "fModel" = 1 AND "fYear" = 1 AND "fBody" = 1
              AND "fSteering" = 1 AND "fTransmission" = 1 AND "fRegion" = 1
            GROUP BY "engine"

            ORDER BY 1, 2
            """).ToListAsync(ct);
    }

    /// <summary>
    /// The vehicles left once every filter is applied, and how many that is.
    /// </summary>
    /// <remarks>
    /// The total comes back on the same pass — two queries can disagree, and
    /// this one decides whether the UI shows the list at all.
    /// </remarks>
    public async Task<FinderPage> VehiclesAsync(FinderFilters f, int limit, CancellationToken ct = default)
    {
        var make = f.Make;
        var series = f.Series;
        var model = f.Model;
        var year = f.Year;
        var bodyType = f.BodyType;
        var steeringSide = f.SteeringSide;
        var transmission = f.Transmission;
        var engine = f.Engine;
        var region = f.Region;

        var rows = await db.Database.SqlQuery<CountedFinderVehicle>($"""
            SELECT COUNT(*) OVER ()  AS "Total",
                   vv."id" AS "VariantId",
                   mk."name" AS "MakeName", mo."name" AS "ModelName", mo."series" AS "Series",
                   vv."name" AS "VariantName", vv."yearFrom" AS "YearFrom", vv."yearTo" AS "YearTo",
                   vv."bodyType" AS "BodyType", vv."steeringSide" AS "SteeringSide",
                   vv."transmission" AS "Transmission", vv."region" AS "Region",
                   vv."engineCode" AS "EngineCode", vv."powerKw" AS "PowerKw", vv."fuel" AS "Fuel"
            FROM "VehicleVariant" vv
            JOIN "VehicleModel" mo ON mo."id" = vv."modelId"
            JOIN "VehicleMake" mk ON mk."id" = mo."makeId"
            WHERE ({make} IS NULL OR mk."name" = {make})
              AND ({series} IS NULL OR mo."series" = {series} OR mo."series" IS NULL)
              AND ({model} IS NULL OR mo."name" = {model})
              AND ({year} IS NULL
                OR (vv."yearFrom" <= {year} AND COALESCE(vv."yearTo", 9999) >= {year}))
              AND ({bodyType} IS NULL OR vv."bodyType" = {bodyType} OR vv."bodyType" IS NULL)
              AND ({steeringSide} IS NULL OR vv."steeringSide" = {steeringSide} OR vv."steeringSide" IS NULL)
              AND ({transmission} IS NULL OR vv."transmission" = {transmission} OR vv."transmission" IS NULL)
              AND ({region} IS NULL OR vv."region" = {region} OR vv."region" IS NULL)
              AND ({engine} IS NULL OR COALESCE(vv."engineCode", vv."name") = {engine})
            -- Ordered so the same search reads the same twice running.
            ORDER BY mk."name" ASC, mo."name" ASC, vv."yearFrom" ASC, vv."name" ASC, vv."id" ASC
            OFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY
            """).ToListAsync(ct);

        return new FinderPage(
            rows.Select(r => r.ToVehicle()).ToList(),
            // No rows means no total to read one off. The window function has
            // nothing to attach it to, which is not the same as it saying zero.
            rows.Count > 0 ? rows[0].Total : 0);
    }
}

public record FinderFilters(
    string? Make = null,
    string? Series = null,
    string? Model = null,
    int? Year = null,
    string? BodyType = null,
    string? SteeringSide = null,
    string? Transmission = null,
    string? Region = null,
    string? Engine = null);

/// <param name="Vehicles">How many vehicles remain if this is chosen on top of what is set.</param>
public record FinderOption(string Field, string Value, int Vehicles);

public record FinderPage(List<FinderVehicle> Vehicles, int Total);

public record FinderVehicle(
    string VariantId,
    string MakeName,
    string ModelName,
    string? Series,
    string VariantName,
    int YearFrom,
    int? YearTo,
    string? BodyType,
    string? SteeringSide,
    string? Transmission,
    string? Region,
    string? EngineCode,
    int? PowerKw,
    string Fuel);

/// <summary>
/// A vehicle carrying the total of the set it came from.
/// </summary>
/// <remarks>
/// Its own type rather than a nullable field on <see cref="FinderVehicle"/>:
/// EF requires every property of the queried type to be present in the result
/// set, and the wire shape must not carry a total on every row.
/// </remarks>
public record CountedFinderVehicle(
    int Total,
    string VariantId,
    string MakeName,
    string ModelName,
    string? Series,
    string VariantName,
    int YearFrom,
    int? YearTo,
    string? BodyType,
    string? SteeringSide,
    string? Transmission,
    string? Region,
    string? EngineCode,
    int? PowerKw,
    string Fuel)
{
    public FinderVehicle ToVehicle() => new(
        VariantId, MakeName, ModelName, Series, VariantName, YearFrom, YearTo,
        BodyType, SteeringSide, Transmission, Region, EngineCode, PowerKw, Fuel);
}
