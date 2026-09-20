using System.Text.Json;
using AutoPartsHub.Domain;
using AutoPartsHub.Domain.Orders;

namespace AutoPartsHub.Tests;

/// <summary>
/// The moves named in the path rather than the body.
/// </summary>
/// <remarks>
/// <c>POST …/approve</c>, <c>…/reject</c> and <c>…/cancel</c> are the shape the
/// backlog specifies, beside the <c>PATCH</c> that names its status in the
/// body. The danger in adding a second shape is not that it does the wrong
/// thing — it is that it does slightly LESS: the same move, missing one of the
/// refusals the first shape makes.
///
/// So these do not assert that approve approves. They assert that the four
/// rules survive the change of shape: the vocabulary, whether the move is open
/// from where the order is, which moves have to say why, and that a tracking
/// number belongs to the act of shipping. Every one of them is a refusal, and
/// a refusal that stops happening is silent.
///
/// <c>PATCH …/shipping</c> is the fourth route and the one that is not a move
/// at all. Its rule is the same one from the other side: before an order has
/// shipped there is nothing to track.
///
/// No database — these are the rules, and they live in a layer that has none.
/// </remarks>
public class OrderVerbTests
{
    private static Validated<StatusChange> Verb(string from, string to, string json = "{}") =>
        OrderStatuses.ReadStatusChange(JsonDocument.Parse(json).RootElement, from, to);

    private static Validated<ShippingChange> Shipping(string from, string json) =>
        OrderStatuses.ReadShippingChange(JsonDocument.Parse(json).RootElement, from);

    private static string Refusal<T>(Validated<T> r)
    {
        Assert.False(r.Ok, "expected a refusal, got success");
        return r.Error!;
    }

    // ------------------------------------------------------- the three moves

    [Fact]
    public void ApproveNeedsNothingSaidAboutIt()
    {
        var change = Verb(OrderStatuses.OrderIsSent, OrderStatuses.Accepted);

        Assert.True(change.Ok, change.Error);
        Assert.Equal("accepted", change.Value!.Status);
        Assert.Null(change.Value.Reason);
    }

    /// <remarks>
    /// The rule that is easiest to lose when a move moves into the path: the
    /// body no longer carries the status, so it is tempting to stop validating
    /// one. An order already shipped cannot be approved, and the sentence says
    /// what it CAN become rather than only that it cannot.
    /// </remarks>
    [Fact]
    public void AMoveThatIsNotOpenFromHereIsStillRefused()
    {
        Assert.Equal(
            "An order that is shipped can only become: delivered, paid.",
            Refusal(Verb(OrderStatuses.Shipped, OrderStatuses.Accepted)));

        Assert.Equal(
            "This order is paid and finished; nothing can move it.",
            Refusal(Verb(OrderStatuses.Paid, OrderStatuses.Cancelled)));
    }

    /// <remarks>
    /// Rejection is only available before acceptance — after that, not
    /// supplying an order is a cancellation, and calling it a rejection would
    /// hide which of the two happened.
    /// </remarks>
    [Fact]
    public void RejectIsStillOnlyAvailableBeforeAcceptance()
    {
        Assert.True(Verb(OrderStatuses.OrderIsSent, OrderStatuses.Rejected,
            """{"reason":"out of stock at every supplier"}""").Ok);

        Assert.Contains("can only become",
            Refusal(Verb(OrderStatuses.Accepted, OrderStatuses.Rejected,
                """{"reason":"changed our mind"}""")));
    }

    /// <remarks>
    /// Nobody can reconstruct afterwards why an order was refused or called
    /// off, so both have to say. The verb routes carry a body for exactly this
    /// and would be pointless if the requirement had been lost with the status.
    /// </remarks>
    [Theory]
    [InlineData(OrderStatuses.OrderIsSent, OrderStatuses.Rejected)]
    [InlineData(OrderStatuses.OrderIsSent, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Accepted, OrderStatuses.Cancelled)]
    public void RejectAndCancelStillHaveToSayWhy(string from, string to)
    {
        Assert.Contains("Say why", Refusal(Verb(from, to)));

        Assert.True(Verb(from, to, """{"reason":"the customer called"}""").Ok);
    }

    [Fact]
    public void AReasonNobodyWouldReadIsStillRefused()
    {
        var tooLong = new string('x', OrderStatuses.MaxReason + 1);

        Assert.Contains("under 300",
            Refusal(Verb(OrderStatuses.OrderIsSent, OrderStatuses.Cancelled,
                $$"""{"reason":"{{tooLong}}"}""")));
    }

    /// <remarks>
    /// There is no <c>POST …/ship</c>, so nothing should be able to smuggle a
    /// tracking number in through one of the three that exist.
    /// </remarks>
    [Fact]
    public void ATrackingNumberCannotRideInOnAVerb()
    {
        Assert.Equal(
            "A tracking number can only be given when the order ships.",
            Refusal(Verb(OrderStatuses.OrderIsSent, OrderStatuses.Accepted,
                """{"trackingNumber":"JD01"}""")));
    }

    /// <summary>
    /// The body half and the path half agree, move for move.
    /// </summary>
    /// <remarks>
    /// The property that matters most and the cheapest to state: for every
    /// status, from every status, naming the move in the path decides exactly
    /// what naming it in the body decides. One reader, reached two ways.
    /// </remarks>
    [Fact]
    public void ThePathAndTheBodyDecideTheSameThing()
    {
        foreach (var from in OrderStatuses.All)
        {
            foreach (var to in OrderStatuses.All)
            {
                const string body = """{"reason":"a reason, in case one is needed"}""";
                var viaPath = Verb(from, to, body);
                var viaBody = OrderStatuses.ReadStatusChange(
                    JsonDocument.Parse($$"""{"status":"{{to}}","reason":"a reason, in case one is needed"}""")
                        .RootElement,
                    from);

                Assert.Equal(viaBody.Ok, viaPath.Ok);
                Assert.Equal(viaBody.Error, viaPath.Error);
                Assert.Equal(viaBody.Value?.Status, viaPath.Value?.Status);
            }
        }
    }

    // ---------------------------------------------------------- the shipping

    /// <remarks>
    /// The same rule the moves enforce, from the other side: before an order
    /// has shipped there is nothing to track, and a number given then is one
    /// the customer would be handed and could not use.
    /// </remarks>
    [Theory]
    [InlineData(OrderStatuses.OrderIsSent)]
    [InlineData(OrderStatuses.Accepted)]
    [InlineData(OrderStatuses.Processing)]
    [InlineData(OrderStatuses.Rejected)]
    [InlineData(OrderStatuses.Cancelled)]
    public void ShippingDetailsAreRefusedBeforeAnOrderHasShipped(string from) =>
        Assert.Contains("once it has shipped", Refusal(Shipping(from, """{"trackingNumber":"JD01"}""")));

    /// <remarks>
    /// And allowed everywhere after. A tracking number is corrected most often
    /// once the customer has looked it up and found nothing, which is after
    /// delivery as readily as before it.
    /// </remarks>
    [Theory]
    [InlineData(OrderStatuses.Shipped)]
    [InlineData(OrderStatuses.Delivered)]
    [InlineData(OrderStatuses.Paid)]
    public void ShippingDetailsAreAllowedOnceItHas(string from)
    {
        var change = Shipping(from, """{"trackingNumber":" JD02 ","carrier":"UPS"}""");

        Assert.True(change.Ok, change.Error);
        Assert.Equal("JD02", change.Value!.TrackingNumber);
        Assert.Equal("UPS", change.Value.Carrier);
    }

    /// <remarks>
    /// Read off the transition map rather than listed, so "has shipped" cannot
    /// drift from what the lifecycle says. Asserted for every status so a new
    /// one added after <c>shipped</c> is not silently left out.
    /// </remarks>
    [Fact]
    public void HasShippedIsExactlyShippedAndWhatFollowsIt()
    {
        var shipped = OrderStatuses.All.Where(OrderStatuses.HasShipped).Order().ToArray();

        Assert.Equal(["delivered", "paid", "shipped"], shipped);
    }

    /// <remarks>
    /// Either field alone is a real correction — a carrier that was recorded
    /// wrong, a number that was. Neither is almost always a caller sending the
    /// wrong field name, and answering "nothing changed" to that reads as
    /// success.
    /// </remarks>
    [Theory]
    [InlineData("""{"trackingNumber":"JD02"}""")]
    [InlineData("""{"carrier":"UPS"}""")]
    public void OneOfTheTwoIsEnough(string json) =>
        Assert.True(Shipping(OrderStatuses.Shipped, json).Ok);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"trackingNumber":null,"carrier":null}""")]
    [InlineData("""{"trackingNumber":"   "}""")]
    [InlineData("""{"status":"delivered"}""")]
    public void AShippingChangeThatChangesNothingIsRefused(string json) =>
        Assert.Contains("trackingNumber", Refusal(Shipping(OrderStatuses.Shipped, json)));
}
