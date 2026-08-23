using AutoPartsHub.Api.Catalogue;
using AutoPartsHub.Api.Data;
using AutoPartsHub.Api.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Endpoints;

public static class VehicleEndpoints
{
    public static void MapVehicleEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/vehicles — the whole make/model/variant tree for the picker.
        // Small enough to send in one go; paginate here if the list ever grows.
        app.MapGet("/api/vehicles", async (AutoPartsContext db) =>
            Results.Ok(new { makes = await VehicleTree(db) }));

        // GET /api/vehicles/vin?vin=WBA3A5C50DF123456
        //
        // Answers with the make and model year the VIN itself carries, plus the
        // vehicles we hold for that make that were built in that year. The model
        // and engine are not readable from a VIN without a licensed database, so
        // the customer picks from those candidates rather than being told a
        // single answer.
        app.MapGet("/api/vehicles/vin", async (string? vin, AutoPartsContext db, CancellationToken ct) =>
        {
            var parsed = Vin.Parse(vin ?? "");
            if (!parsed.Success) return Results.BadRequest(new { error = parsed.Error });

            var reading = parsed.Value!;

            var make = await db.VehicleMakes
                .Where(m => m.WmiCodes.Contains(reading.Wmi))
                .Select(m => new { m.Id, m.Name })
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (make is null)
            {
                // Recorded even here. A manufacturer we do not carry is the
                // clearest case of a lookup that failed a customer, and
                // leaving it out would make the numbers look better than the
                // service is.
                await VinLog.RecordAsync(db, reading.Vin, reading.ModelYear, null, 0, ct);

                return Results.Ok(new VinResponse(
                    reading.Vin, reading.Wmi, reading.ModelYear, reading.ModelYearIsEstimate,
                    reading.CheckDigitValid, null, [],
                    $"We do not carry parts for the manufacturer behind {reading.Wmi} yet."));
            }

            // The tree is small enough to read whole and narrow here — see
            // /api/vehicles, which sends the same thing to the picker on every
            // page load.
            var models = (await VehicleTree(db)).FirstOrDefault(m => m.Id == make.Id)?.Models ?? [];
            var year = reading.ModelYear;

            // Keep the variants whose production years cover the VIN's model
            // year. With no year to go on, offer the make's whole range rather
            // than nothing.
            var candidates = models.SelectMany(model => model.Variants
                .Where(v => year is null || (v.YearFrom <= year && (v.YearTo ?? 9999) >= year))
                .Select(v => new VinCandidate(
                    v.Id, model.Id, $"{make.Name} {model.Name} {v.Name}",
                    v.EngineCode, v.Fuel, v.YearFrom, v.YearTo)))
                .ToList();

            // After the answer is assembled and before it is sent — a
            // counter, whose every failure is swallowed. What is written down
            // is a KIND of vehicle, never a vehicle: positions 1-8 and the
            // model year, never the serial. The response is unchanged, which
            // is why the comparison harness sees nothing new here.
            await VinLog.RecordAsync(db, reading.Vin, year, make.Name, candidates.Count, ct);

            return Results.Ok(new VinResponse(
                reading.Vin, reading.Wmi, year, reading.ModelYearIsEstimate, reading.CheckDigitValid,
                new MakeRef(make.Id, make.Name),
                candidates,
                candidates.Count == 0
                    ? $"{make.Name} is the manufacturer, but we hold no {year?.ToString() ?? ""} models for it."
                    : null));
        });
    }

    /// <summary>
    /// The make / model / variant tree the vehicle picker walks.
    /// </summary>
    /// <remarks>
    /// Read as three flat lists and assembled here rather than as nested
    /// joins: a join would repeat every make once per variant and have to be
    /// folded back together anyway, and three ordered reads are easier to see
    /// the cost of.
    /// </remarks>
    private static async Task<List<MakeNode>> VehicleTree(AutoPartsContext db)
    {
        var makes = await db.VehicleMakes.OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name }).AsNoTracking().ToListAsync();
        var models = await db.VehicleModels.OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.MakeId, m.Name, m.YearFrom, m.YearTo })
            .AsNoTracking().ToListAsync();
        var variants = await db.VehicleVariants.OrderBy(v => v.Name)
            .Select(v => new { v.Id, v.ModelId, v.Name, v.EngineCode, v.PowerKw, v.Fuel, v.YearFrom, v.YearTo })
            .AsNoTracking().ToListAsync();

        var variantsByModel = variants
            .GroupBy(v => v.ModelId)
            .ToDictionary(g => g.Key, g => g
                .Select(v => new VariantNode(v.Id, v.Name, v.EngineCode, v.PowerKw, v.Fuel, v.YearFrom, v.YearTo))
                .ToList());

        var modelsByMake = models
            .GroupBy(m => m.MakeId)
            .ToDictionary(g => g.Key, g => g
                .Select(m => new ModelNode(
                    m.Id, m.Name, m.YearFrom, m.YearTo,
                    variantsByModel.GetValueOrDefault(m.Id) ?? []))
                .ToList());

        return makes
            .Select(m => new MakeNode(m.Id, m.Name, modelsByMake.GetValueOrDefault(m.Id) ?? []))
            .ToList();
    }
}

public record MakeNode(string Id, string Name, List<ModelNode> Models);

public record ModelNode(string Id, string Name, int YearFrom, int? YearTo, List<VariantNode> Variants);

public record VariantNode(
    string Id, string Name, string? EngineCode, int? PowerKw, string Fuel, int YearFrom, int? YearTo);

public record MakeRef(string Id, string Name);

public record VinCandidate(
    string VariantId, string ModelId, string Label, string? EngineCode, string Fuel,
    int YearFrom, int? YearTo);

public record VinResponse(
    string Vin,
    string Wmi,
    int? ModelYear,
    bool ModelYearIsEstimate,
    bool? CheckDigitValid,
    MakeRef? Make,
    List<VinCandidate> Candidates,
    string? Message);
