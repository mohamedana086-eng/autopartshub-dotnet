using AutoPartsHub.Api.Vehicles;
using AutoPartsHub.Domain.Vehicles;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// GET /api/vehicles/find — for the customer who does not know their VIN.
/// </summary>
/// <remarks>
/// Takes any subset of the nine fields, in any order, and answers with every
/// option still open plus the vehicles that are left.
///
/// Order is not imposed because it cannot be: somebody standing in front of a
/// car knows its shape and which side the wheel is on before they know which
/// series it belongs to, and a form that asks for the series first is asking
/// them to guess.
///
/// Each list leaves its own filter out — see VehicleFinder.
/// </remarks>
public static class VehicleFinderEndpoints
{
    public static void MapVehicleFinderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/vehicles/find", async (
            HttpContext http, VehicleFinder finder, CancellationToken ct) =>
        {
            var query = http.Request.Query;

            string? Param(string key) =>
                query[key].ToString().Trim() is { Length: > 0 } v ? v : null;

            var given = new Dictionary<string, string>();
            var ignored = new List<object>();

            foreach (var field in VehicleFields.All)
            {
                if (Param(field) is not { } raw) continue;

                if (!VehicleFields.IsKnownValue(field, raw))
                {
                    // Reported rather than refused. A stale link carrying a
                    // body type that has since been renamed should still find
                    // the customer's car by the other eight fields.
                    ignored.Add(new { field, value = raw });
                    continue;
                }
                given[field] = raw;
            }

            var filters = new FinderFilters(
                Make: given.GetValueOrDefault("make"),
                Series: given.GetValueOrDefault("series"),
                Model: given.GetValueOrDefault("model"),
                Year: given.TryGetValue("year", out var y) ? int.Parse(y) : null,
                BodyType: given.GetValueOrDefault("bodyType"),
                SteeringSide: given.GetValueOrDefault("steeringSide"),
                Transmission: given.GetValueOrDefault("transmission"),
                Region: given.GetValueOrDefault("region"),
                Engine: given.GetValueOrDefault("engine"));

            var options = await finder.OptionsAsync(filters, ct);
            var page = await finder.VehiclesAsync(filters, VehicleFields.ShowVehiclesAt, ct);

            // Grouped by field here rather than shaped in SQL: one flat result
            // set is one round trip, and turning it into nine lists is a loop.
            var fields = VehicleFields.All.Select(field => new
            {
                field,
                label = VehicleFields.Labels[field],
                chosen = given.GetValueOrDefault(field),
                options = options
                    .Where(o => o.Field == field)
                    .Select(o => new
                    {
                        value = o.Value,
                        label = VehicleFields.ValueLabel(field, o.Value),
                        vehicles = o.Vehicles,
                    })
                    // Year descending — a customer thinks of a recent car
                    // first. Everything else by how many vehicles it leaves,
                    // then by name, so the same question reads the same twice.
                    .OrderBy(o => field == "year" ? -int.Parse(o.value) : 0)
                    .ThenByDescending(o => field == "year" ? 0 : o.vehicles)
                    .ThenBy(o => field == "year" ? "" : o.label, StringComparer.InvariantCulture)
                    .ToList(),
            }).ToList();

            return Results.Ok(new
            {
                filters = new
                {
                    make = filters.Make,
                    series = filters.Series,
                    model = filters.Model,
                    year = filters.Year,
                    bodyType = filters.BodyType,
                    steeringSide = filters.SteeringSide,
                    transmission = filters.Transmission,
                    region = filters.Region,
                    engine = filters.Engine,
                },
                ignored,
                fields,
                count = page.Total,
                showVehiclesAt = VehicleFields.ShowVehiclesAt,
                // Below the threshold, showing the list beats asking another
                // question: a customer who can see eight cars finds theirs by
                // reading, and another dropdown is another chance to pick
                // wrong and see nothing at all.
                vehicles = page.Total <= VehicleFields.ShowVehiclesAt ? page.Vehicles : [],
            });
        });
    }
}
