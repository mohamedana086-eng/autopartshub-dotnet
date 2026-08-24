using AutoPartsHub.Api.Pricing;

namespace AutoPartsHub.Api.Endpoints;

/// <summary>
/// Runs the pricing engine on inputs handed to it, for comparison against the
/// TypeScript engine it was ported from.
/// </summary>
/// <remarks>
/// DEVELOPMENT ONLY — see the guard at the call site in Program.cs. It takes
/// arbitrary rules and prices with them, which is exactly what the admin's
/// markup screen is for and exactly what no unauthenticated caller should be
/// able to do.
///
/// It exists because the engine is pure on both sides: the same inputs can be
/// pushed through both implementations and every field of the result compared.
/// That catches what a hand-ported example never would — a rounding half-case,
/// a tie broken the other way, a percentage formatted differently inside the
/// sentence the customer reads.
/// </remarks>
public static class PricingProbeEndpoints
{
    public static void MapPricingProbe(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/price", (PriceProbeRequest body) =>
        {
            // Specificity is recomputed here rather than taken from the body,
            // which is what a save does. It also means the comparison covers
            // both implementations of the arithmetic: if this port scored a
            // list of three suppliers differently from the other, the ranking
            // would change and the diff would show it as a different price.
            var rules = body.Rules
                .Select(r => r with
                {
                    Specificity = MarkupDimensions.SpecificityOf(
                        r.Conditions,
                        r.PurchasePriceFrom is not null || r.PurchasePriceTo is not null),
                })
                .ToList();

            return Results.Ok(PricingEngine.Resolve(body.Context, rules));
        });
    }
}

public record PriceProbeRequest(PricingContext Context, List<MarkupRule> Rules);
