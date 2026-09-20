using System.Text.Json;
using AutoPartsHub.Api.Admin;
using AutoPartsHub.Domain.Orders;
using AutoPartsHub.Domain;
using Microsoft.AspNetCore.Http;

namespace AutoPartsHub.Tests;

/// <summary>
/// What can happen to an order.
/// </summary>
/// <remarks>
/// The order used to have four statuses and no rules at all about moving
/// between them: any status could be set from any other, so <c>paid</c> could
/// go back to <c>order_is_sent</c> and the shelves would be adjusted twice for
/// a shipment that happened once. Most of what is pinned down here is the
/// absence of that — and it has to match the other API answer for answer,
/// because both write to the same table and both put these sentences in front
/// of an admin.
/// </remarks>
public class OrderLifecycleTests
{
    private static Validated<StatusChange> Change(string json, string from) =>
        OrderStatuses.ReadStatusChange(JsonDocument.Parse(json).RootElement, from);

    private static StatusChange Ok(Validated<StatusChange> r)
    {
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string Err(Validated<StatusChange> r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    /* ------------------------------------------------- the vocabulary --- */

    [Fact]
    public void KnowsItsOwnStatusesAndNothingElse()
    {
        Assert.True(OrderStatuses.IsKnown("accepted"));
        Assert.False(OrderStatuses.IsKnown("ACCEPTED"));
        Assert.False(OrderStatuses.IsKnown("almost_shipped"));
    }

    [Fact]
    public void KeptEveryStatusTheFourStatusVersionHad()
    {
        // Rows written before the lifecycle existed carry these, and a
        // vocabulary that dropped one would strand them.
        foreach (var old in new[] { "order_is_sent", "processing", "shipped", "paid" })
        {
            Assert.Contains(old, OrderStatuses.All);
        }
    }

    /* --------------------------------------------- which moves are legal --- */

    [Fact]
    public void AcceptsOrRefusesAnOrderThatHasJustArrived()
    {
        Assert.True(OrderStatuses.CanMove("order_is_sent", "accepted"));
        Assert.True(OrderStatuses.CanMove("order_is_sent", "rejected"));
    }

    [Fact]
    public void WillNotLetAnAcceptedOrderBeRefused()
    {
        // Once we have said we will supply it, not supplying it is a
        // cancellation. Calling it a rejection would hide which of the two
        // happened, and they are answerable to different people.
        Assert.False(OrderStatuses.CanMove("accepted", "rejected"));
        Assert.True(OrderStatuses.CanMove("accepted", "cancelled"));
    }

    [Fact]
    public void WillNotCancelAnOrderThatHasAlreadyGoneOut()
    {
        // Goods that have left come back as a return, which is a different
        // event with a different effect on stock. Reusing `cancelled` for it
        // would put the units back on the shelf without anyone receiving them.
        Assert.False(OrderStatuses.CanMove("shipped", "cancelled"));
        Assert.False(OrderStatuses.CanMove("delivered", "cancelled"));
        Assert.False(OrderStatuses.CanMove("paid", "cancelled"));
    }

    [Fact]
    public void LetsAnInvoiceBeSettledBeforeADeliveryIsConfirmed()
    {
        Assert.True(OrderStatuses.CanMove("shipped", "paid"));
        Assert.True(OrderStatuses.CanMove("delivered", "paid"));
    }

    [Fact]
    public void NeverGoesBackwards()
    {
        Assert.False(OrderStatuses.CanMove("shipped", "processing"));
        Assert.False(OrderStatuses.CanMove("paid", "order_is_sent"));
        Assert.False(OrderStatuses.CanMove("processing", "order_is_sent"));
    }

    [Fact]
    public void TreatsTheThreeClosedStatusesAsClosed()
    {
        foreach (var final in new[] { "paid", "rejected", "cancelled" })
        {
            Assert.True(OrderStatuses.IsFinal(final));
            Assert.Empty(OrderStatuses.MovesFrom(final));
        }
    }

    [Fact]
    public void LeavesEveryOtherStatusSomewhereToGo()
    {
        foreach (var status in OrderStatuses.All)
        {
            if (OrderStatuses.IsFinal(status)) continue;
            Assert.NotEmpty(OrderStatuses.MovesFrom(status));
        }
    }

    /* ------------------------------ what a status does to the shelves --- */

    [Fact]
    public void HoldsStockFromTheMomentItIsPlacedUntilItGoesOut()
    {
        Assert.Equal(StockEffect.Holding, OrderStatuses.EffectOf("order_is_sent"));
        Assert.Equal(StockEffect.Holding, OrderStatuses.EffectOf("accepted"));
        Assert.Equal(StockEffect.Holding, OrderStatuses.EffectOf("processing"));
    }

    [Fact]
    public void CountsPaidAsGoneNotAsStillOnTheShelf()
    {
        // An order is not marked paid before it is fulfilled, and treating it
        // as still-on-the-shelf would put the units back the moment the
        // invoice was settled.
        Assert.Equal(StockEffect.Gone, OrderStatuses.EffectOf("paid"));
        Assert.Equal(StockEffect.Gone, OrderStatuses.EffectOf("delivered"));
    }

    [Fact]
    public void DrawsStockDownWhenAnOrderShipsPromiseAndShelfTogether()
    {
        // Dropping only one would leave either phantom stock or a permanent
        // promise against it.
        Assert.Equal(new ShelfChange(-1, -1), OrderStatuses.ShelfChangeFor("processing", "shipped"));
    }

    [Fact]
    public void ReleasesThePromiseWhenAnOrderIsCalledOffAndTakesNoStock()
    {
        // The goods never left. This is the case the old two-state version
        // could not express, and without it the units stayed reserved for an
        // order nobody would ever pick.
        Assert.Equal(new ShelfChange(0, -1), OrderStatuses.ShelfChangeFor("processing", "cancelled"));
        Assert.Equal(new ShelfChange(0, -1), OrderStatuses.ShelfChangeFor("order_is_sent", "rejected"));
    }

    [Fact]
    public void MovesNothingBetweenTwoStatusesThatHoldStockTheSameWay()
    {
        Assert.Equal(new ShelfChange(0, 0), OrderStatuses.ShelfChangeFor("order_is_sent", "accepted"));
        Assert.Equal(new ShelfChange(0, 0), OrderStatuses.ShelfChangeFor("accepted", "processing"));
        Assert.Equal(new ShelfChange(0, 0), OrderStatuses.ShelfChangeFor("shipped", "paid"));
    }

    [Fact]
    public void MovesNothingAtAllForAMoveToWhereTheOrderAlreadyIs()
    {
        foreach (var status in OrderStatuses.All)
        {
            Assert.Equal(new ShelfChange(0, 0), OrderStatuses.ShelfChangeFor(status, status));
        }
    }

    [Fact]
    public void NeverRaisesAReservationOnAnyMoveAnOrderCanActuallyMake()
    {
        // `reserved <= quantity` is a CHECK on the table, and the way to break
        // it is to promise more than is there. No legal move does: every one
        // of them either leaves the promise alone or lowers it.
        foreach (var from in OrderStatuses.All)
        {
            foreach (var to in OrderStatuses.MovesFrom(from))
            {
                Assert.True(OrderStatuses.ShelfChangeFor(from, to).Reserved <= 0);
            }
        }
    }

    [Fact]
    public void WouldRaiseOneOnlyByUndoingAClosureWhichNoLegalMoveDoes()
    {
        // The arithmetic CAN produce a rise — it is what reversing a
        // cancellation would mean — and the reason it never happens is the
        // transition table, not the shelf positions.
        Assert.Equal(new ShelfChange(0, 1), OrderStatuses.ShelfChangeFor("cancelled", "processing"));
        Assert.False(OrderStatuses.CanMove("cancelled", "processing"));
    }

    /* ------------------------------------- reading a requested change --- */

    [Fact]
    public void RefusesAStatusNobodyHasHeardOf()
    {
        Assert.Contains("Status must be one of",
            Err(Change("""{"status":"nearly"}""", "order_is_sent")));
    }

    [Fact]
    public void RefusesALegalStatusThatIsNotReachableFromHere()
    {
        Assert.Equal(
            "An order that is shipped can only become: delivered, paid.",
            Err(Change("""{"status":"processing"}""", "shipped")));
    }

    [Fact]
    public void SaysAnOrderIsFinishedRatherThanListingNowhereToGo()
    {
        Assert.Equal(
            "This order is paid and finished; nothing can move it.",
            Err(Change("""{"status":"processing"}""", "paid")));
    }

    [Fact]
    public void AcceptsSavingTheStatusItAlreadyHas()
    {
        // Not an error: pressing save twice should not fail, and the shelf
        // change for a move to where you already are is zero anyway.
        Assert.Equal("processing", Ok(Change("""{"status":"processing"}""", "processing")).Status);
    }

    [Fact]
    public void WillNotLetAnOrderBeRefusedWithoutSayingWhy()
    {
        Assert.Equal(
            "Say why the order was rejected. Nobody can work it out from the row afterwards.",
            Err(Change("""{"status":"rejected"}""", "order_is_sent")));
        Assert.Contains("Say why",
            Err(Change("""{"status":"cancelled","reason":"   "}""", "processing")));
    }

    [Fact]
    public void TakesAReasonAndKeepsIt()
    {
        var value = Ok(Change(
            """{"status":"rejected","reason":"  Account on stop.  "}""", "order_is_sent"));

        Assert.Equal("Account on stop.", value.Reason);
    }

    [Fact]
    public void AllowsAReasonOnAStatusThatDoesNotRequireOne()
    {
        // An admin noting "customer asked us to hold it" against a processing
        // order is useful, and there is no reason to forbid it.
        Assert.Equal("On hold",
            Ok(Change("""{"status":"processing","reason":"On hold"}""", "accepted")).Reason);
    }

    [Fact]
    public void RefusesAReasonNobodyWouldRead()
    {
        var reason = new string('x', OrderStatuses.MaxReason + 1);
        var json = JsonSerializer.Serialize(new { status = "cancelled", reason });

        Assert.Contains("under 300", Err(Change(json, "accepted")));
    }

    [Fact]
    public void TakesATrackingNumberWhenTheOrderShips()
    {
        var value = Ok(Change(
            """{"status":"shipped","trackingNumber":" JD01 ","carrier":"DHL"}""", "processing"));

        Assert.Equal("JD01", value.TrackingNumber);
        Assert.Equal("DHL", value.Carrier);
    }

    [Fact]
    public void RefusesATrackingNumberOnAnyOtherMove()
    {
        // A number given while the order is still being picked is a number the
        // customer would be handed and could not use.
        Assert.Equal(
            "A tracking number can only be given when the order ships.",
            Err(Change("""{"status":"accepted","trackingNumber":"JD01"}""", "order_is_sent")));
    }

    /* --------------------------------------------------- the filters --- */

    // A plain lookup. It used to build a QueryCollection, because Read took
    // one; now that the rules live in a layer with no web framework in it,
    // neither does the test.
    private static Validated<OrderFilter> Filters(Dictionary<string, string> q) =>
        OrderFilters.Read(key => q.GetValueOrDefault(key));

    private static OrderFilter OkFilters(Dictionary<string, string> q)
    {
        var r = Filters(q);
        Assert.True(r.Ok, $"expected success, got: {r.Error}");
        return r.Value!;
    }

    private static string ErrFilters(Dictionary<string, string> q)
    {
        var r = Filters(q);
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    [Fact]
    public void AsksForNothingWhenNothingWasAsked()
    {
        Assert.Equal(new OrderFilter(null, null, null, null), OkFilters([]));
    }

    [Fact]
    public void CoversTheWholeOfTheLastDayNamed()
    {
        // The 28th at midnight, compared exclusively — which covers the 27th
        // without depending on how many decimals a timestamp carries.
        var before = OkFilters(new() { ["to"] = "2026-08-27" }).Before;

        Assert.Equal(new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc), before);
    }

    [Fact]
    public void ReadsABareDateAsUtcNotAsTheServersIdeaOfMidnight()
    {
        // The stored timestamps are UTC. Letting a machine's own zone decide
        // which orders fall in August would make the same filter answer
        // differently on two servers.
        var from = OkFilters(new() { ["from"] = "2026-08-01" }).From;

        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), from);
    }

    [Fact]
    public void LeavesAFullTimestampExactlyWhereItWasPut()
    {
        var before = OkFilters(new() { ["to"] = "2026-08-27T09:30:00.000Z" }).Before;

        Assert.Equal(new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Utc), before);
    }

    [Fact]
    public void RefusesADateItCannotReadInsteadOfIgnoringIt()
    {
        // A filter that silently does not apply is worse than one that fails:
        // the numbers still look plausible.
        Assert.Equal("The `from` date could not be read. Use YYYY-MM-DD.",
            ErrFilters(new() { ["from"] = "last tuesday" }));
        Assert.Equal("The `to` date could not be read. Use YYYY-MM-DD.",
            ErrFilters(new() { ["to"] = "27/08/2026" }));
    }

    [Fact]
    public void RefusesARangeThatEndsBeforeItStarts()
    {
        Assert.Contains("not after",
            ErrFilters(new() { ["from"] = "2026-08-27", ["to"] = "2026-08-01" }));
    }

    [Fact]
    public void AcceptsARangeOfASingleDay()
    {
        var f = OkFilters(new() { ["from"] = "2026-08-27", ["to"] = "2026-08-27" });

        Assert.Equal(new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc), f.From);
        Assert.Equal(new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc), f.Before);
    }

    [Fact]
    public void RefusesAStatusOutsideTheVocabulary()
    {
        Assert.Contains("Status must be one of", ErrFilters(new() { ["status"] = "nearly" }));
    }

    [Fact]
    public void TakesAStatusAndAManager()
    {
        var f = OkFilters(new() { ["status"] = "shipped", ["managerId"] = "c-sales" });

        Assert.Equal("shipped", f.Status);
        Assert.Equal("c-sales", f.ManagerId);
    }
}
