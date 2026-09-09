namespace AutoPartsHub.Application.Abstractions;

/// <summary>What Odoo was told about one order.</summary>
/// <param name="OdooOrderId">Their id for it. Kept so the poller can ask after it.</param>
public record OdooOrderAccepted(string OdooOrderId);

/// <summary>One order's state over there.</summary>
public record OdooOrderState(string OdooOrderId, string State);

/// <summary>
/// The accounting system orders are handed to.
/// </summary>
/// <remarks>
/// Serves T-162 and T-163, and stands in for BLK-002 — there is no real Odoo
/// to talk to yet, and the point of naming the interface now is that connecting
/// one becomes a class in Infrastructure rather than a change to how orders
/// are approved.
///
/// NOTHING HERE IS CALLED FROM A REQUEST
/// -------------------------------------
/// Approving an order writes the approval and an outbox row in one
/// transaction, and the worker drains the outbox. So an Odoo that is down
/// delays a hand-off and does not refuse an approval — which is the behaviour
/// the acceptance criterion names, and it is only achievable if no request
/// path ever awaits this.
///
/// <see cref="SendOrderAsync"/> is expected to be idempotent on
/// <paramref name="orderId"/>: the outbox retries, and a retry after a reply
/// that was lost in transit must not raise a second sales order.
/// </remarks>
public interface IOdooClient
{
    Task<OdooOrderAccepted> SendOrderAsync(string orderId, CancellationToken ct = default);

    /// <summary>
    /// The states of the orders named, in one call.
    /// </summary>
    /// <remarks>
    /// A batch because the poller has hundreds of open orders and runs every
    /// five minutes; one call each would be the integration that takes the
    /// external system down.
    /// </remarks>
    Task<IReadOnlyList<OdooOrderState>> GetStatesAsync(
        IReadOnlyList<string> odooOrderIds, CancellationToken ct = default);
}
