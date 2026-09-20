using System.Text.Json;
using AutoPartsHub.Domain;

namespace AutoPartsHub.Domain.Orders;

/// <summary>What an order in a given status is doing to the shelves.</summary>
/// <remarks>
/// Three states, not two. The old code asked one question — has it shipped —
/// which was enough while the only way out of an order was through it. Calling
/// an order off is a third answer: the goods never left, so <c>quantity</c> is
/// untouched, but the promise against them has to end or the units stay
/// reserved for an order nobody will ever pick.
/// </remarks>
public enum StockEffect
{
    /// <summary>The units are promised to this order and still on the shelf.</summary>
    Holding,
    /// <summary>They have left: quantity and reservation both drawn down.</summary>
    Gone,
    /// <summary>Neither promised nor gone.</summary>
    Released,
}

/// <summary>How far the shelf moves, as a multiple of what the order allocated.</summary>
public record ShelfChange(int Quantity, int Reserved);

/// <summary>A requested status change, once it has been read and allowed.</summary>
/// <param name="Reason">Required on rejection and cancellation, a note elsewhere.</param>
/// <param name="TrackingNumber">Set only on the move to <c>shipped</c>.</param>
public record StatusChange(string Status, string? Reason, string? TrackingNumber, string? Carrier);

/// <summary>
/// A correction to where a shipment is, with no status change.
/// </summary>
/// <remarks>
/// Separate from <see cref="StatusChange"/> because it answers a different
/// question. A tracking number given on the move to <c>shipped</c> is part of
/// that move; a tracking number given afterwards is a correction — the carrier
/// reissued it, or somebody typed it wrong — and the order does not move for
/// it. Folding the second into the first would mean re-saving a status to
/// change a number, which writes a status-change row that did not happen.
/// </remarks>
public record ShippingChange(string? TrackingNumber, string? Carrier);

/// <summary>
/// What can happen to an order, in what order, and what it does to the shelves.
/// </summary>
/// <remarks>
/// No database in it, so both APIs read the same answers and both can be
/// tested without one. The order used to have four statuses and no rules about
/// moving between them — any status could be set from any other, an order
/// could go from <c>paid</c> back to <c>order_is_sent</c>, and there was no
/// way at all to say that a customer changed their mind or that we would not
/// supply them.
/// </remarks>
public static class OrderStatuses
{
    public const string OrderIsSent = "order_is_sent";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Processing = "processing";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string Paid = "paid";
    public const string Cancelled = "cancelled";

    /// <summary>The vocabulary, in the order the other API lists it.</summary>
    public static readonly string[] All =
        [OrderIsSent, Accepted, Rejected, Processing, Shipped, Delivered, Paid, Cancelled];

    public static bool IsKnown(string value) => All.Contains(value);

    /// <summary>
    /// Where an order may go from where it is.
    /// </summary>
    /// <remarks>
    /// Two rules are worth reading off it. <b>Rejection is only available
    /// before acceptance</b> — once we have said we will supply an order, not
    /// supplying it is a cancellation, and calling it a rejection would hide
    /// which of the two happened. And <b>nothing can be cancelled after it has
    /// shipped</b>: goods that have left come back as a return, which is a
    /// different event with a different effect on stock, and quietly reusing
    /// <c>cancelled</c> for it would put the units back on the shelf without
    /// anyone receiving them.
    /// </remarks>
    private static readonly Dictionary<string, string[]> Next = new()
    {
        [OrderIsSent] = [Accepted, Rejected, Cancelled],
        [Accepted] = [Processing, Cancelled],
        [Processing] = [Shipped, Cancelled],
        // Paid without a delivery confirmation is ordinary: an invoice can be
        // settled before anybody records that the box arrived.
        [Shipped] = [Delivered, Paid],
        [Delivered] = [Paid],
        [Paid] = [],
        [Rejected] = [],
        [Cancelled] = [],
    };

    public static bool CanMove(string from, string to) => MovesFrom(from).Contains(to);

    /// <summary>Where this order could go, for a screen that offers the choices.</summary>
    public static string[] MovesFrom(string from) =>
        Next.TryGetValue(from, out var open) ? open : [];

    /// <summary>
    /// Which statuses close an order for good.
    /// </summary>
    /// <remarks>
    /// The same list read the other way, so a screen and a guard cannot
    /// disagree about whether an order is finished.
    /// </remarks>
    public static bool IsFinal(string status) => MovesFrom(status).Length == 0;

    /// <summary>
    /// The ones that have to say why.
    /// </summary>
    /// <remarks>
    /// An order we refused and an order the customer called off are the two
    /// whose reason nobody can reconstruct afterwards from the row. Every other
    /// status is self-explanatory — <c>shipped</c> does not need a sentence —
    /// so a reason is optional there and required here, and the database
    /// carries the same rule.
    /// </remarks>
    public static bool NeedsReason(string status) => status is Rejected or Cancelled;

    public static StockEffect EffectOf(string status) => status switch
    {
        OrderIsSent or Accepted or Processing => StockEffect.Holding,
        // `paid` counts as gone: an order is not marked paid before it is
        // fulfilled, and treating it as still-on-the-shelf would put the units
        // back the moment the invoice was settled.
        Shipped or Delivered or Paid => StockEffect.Gone,
        _ => StockEffect.Released,
    };

    /// <summary>
    /// What each effect costs the shelf, as a multiple of what was allocated.
    /// </summary>
    /// <remarks>
    /// Expressed as a position rather than as a move: every transition is then
    /// the difference between two positions, so there are three numbers to get
    /// right instead of nine, and no pair of statuses can be handled
    /// inconsistently because no pair is handled at all. <c>Released</c> is the
    /// origin — nothing promised, nothing taken.
    /// </remarks>
    public static ShelfChange PositionOf(StockEffect effect) => effect switch
    {
        StockEffect.Holding => new ShelfChange(0, 1),
        StockEffect.Gone => new ShelfChange(-1, 0),
        _ => new ShelfChange(0, 0),
    };

    /// <summary>How far the shelf moves when an order goes from one status to another.</summary>
    public static ShelfChange ShelfChangeFor(string from, string to)
    {
        var before = PositionOf(EffectOf(from));
        var after = PositionOf(EffectOf(to));

        return new ShelfChange(after.Quantity - before.Quantity, after.Reserved - before.Reserved);
    }

    /// <summary>The longest a reason may be. Long enough to explain, short enough to read.</summary>
    public const int MaxReason = 300;

    /// <summary>
    /// Reads a requested status change against where the order actually is.
    /// </summary>
    /// <remarks>
    /// The current status is part of the question, not a check performed later:
    /// "processing" is a valid status and an invalid destination for an order
    /// that has already shipped, and the two refusals read differently on
    /// purpose.
    /// </remarks>
    public static Validated<StatusChange> ReadStatusChange(JsonElement body, string from) =>
        ReadStatusChange(body, from, JsonValues.AsString(JsonValues.Get(body, "status")).Trim());

    /// <summary>
    /// The same, with the destination given rather than read from the body.
    /// </summary>
    /// <remarks>
    /// For the routes that name the move — <c>POST …/approve</c>,
    /// <c>…/reject</c>, <c>…/cancel</c> — where the status is in the path and
    /// the body carries only a reason.
    ///
    /// An overload rather than a second reader, so that every rule stays in
    /// one place: what the vocabulary is, whether the move is open from here,
    /// which moves have to say why, and that tracking belongs to the act of
    /// shipping. A verb route with its own copy of those would be the version
    /// that drifts, and it would drift silently — each rule it lost is a
    /// refusal that stops happening.
    /// </remarks>
    public static Validated<StatusChange> ReadStatusChange(JsonElement body, string from, string status)
    {
        if (!IsKnown(status))
        {
            return Validation.Fail<StatusChange>(
                $"Status must be one of: {string.Join(", ", All)}.");
        }

        var reason = Text(JsonValues.Get(body, "reason"));

        if (status == from)
        {
            // Not an error. Saving the screen twice should not be a failure,
            // and the shelf change for a move to where you already are is zero.
            return Validation.Ok(new StatusChange(status, reason, null, null));
        }

        if (!CanMove(from, status))
        {
            var open = MovesFrom(from);
            return Validation.Fail<StatusChange>(open.Length == 0
                ? $"This order is {from} and finished; nothing can move it."
                : $"An order that is {from} can only become: {string.Join(", ", open)}.");
        }

        if (NeedsReason(status) && reason is null)
        {
            return Validation.Fail<StatusChange>(
                $"Say why the order was {status}. Nobody can work it out from the row afterwards.");
        }
        if (reason is not null && reason.Length > MaxReason)
        {
            return Validation.Fail<StatusChange>($"Keep the reason under {MaxReason} characters.");
        }

        var trackingNumber = Text(JsonValues.Get(body, "trackingNumber"));
        var carrier = Text(JsonValues.Get(body, "carrier"));

        // Tracking belongs to the act of shipping. Accepting it on any other
        // move would let an order carry a tracking number while still being
        // picked, which is a number a customer would be given and could not use.
        if ((trackingNumber is not null || carrier is not null) && status != Shipped)
        {
            return Validation.Fail<StatusChange>(
                "A tracking number can only be given when the order ships.");
        }

        return Validation.Ok(new StatusChange(status, reason, trackingNumber, carrier));
    }

    /// <summary>Whether the goods have left, in any of the three ways that
    /// means.</summary>
    /// <remarks>
    /// Read off the transition map rather than listed, so it cannot disagree
    /// with it: <c>shipped</c> and everything reachable from it. Today that is
    /// <c>delivered</c> and <c>paid</c>.
    /// </remarks>
    public static bool HasShipped(string status) =>
        status == Shipped || MovesFrom(Shipped).Contains(status)
        || MovesFrom(Shipped).Any(s => MovesFrom(s).Contains(status));

    /// <summary>
    /// Reads a correction to a shipment that has already left.
    /// </summary>
    /// <remarks>
    /// Only after the order has shipped. Before that there is nothing to
    /// track, and a tracking number on an order still being picked is a number
    /// a customer would be given and could not use — the same rule
    /// <see cref="ReadStatusChange(JsonElement, string, string)"/> enforces
    /// from the other side.
    ///
    /// At least one of the two has to be given. A request that changes nothing
    /// is not an error worth refusing on its own, but here it almost always
    /// means the caller sent the wrong field name, and answering "nothing
    /// changed" to that reads as success.
    /// </remarks>
    public static Validated<ShippingChange> ReadShippingChange(JsonElement body, string from)
    {
        if (!HasShipped(from))
        {
            return Validation.Fail<ShippingChange>(
                $"This order is {from}. A tracking number can only be set once it has shipped.");
        }

        var trackingNumber = Text(JsonValues.Get(body, "trackingNumber"));
        var carrier = Text(JsonValues.Get(body, "carrier"));

        if (trackingNumber is null && carrier is null)
        {
            return Validation.Fail<ShippingChange>(
                "Give a trackingNumber, a carrier, or both.");
        }

        return Validation.Ok(new ShippingChange(trackingNumber, carrier));
    }

    private static string? Text(JsonElement? element)
    {
        var trimmed = JsonValues.AsString(element).Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }
}
