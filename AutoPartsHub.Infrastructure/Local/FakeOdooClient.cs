using AutoPartsHub.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoPartsHub.Infrastructure.Local;

/// <summary>
/// Accepts every order and remembers what it was told.
/// </summary>
/// <remarks>
/// BLK-002 is waiting on somebody else, and the outbox, the poller and the
/// order lifecycle are not. This is what lets all three be built and tested
/// against a contract instead of against nothing.
///
/// Two behaviours are real rather than convenient, because they are the ones
/// the calling code has to be written against:
///
/// <list type="bullet">
///   <item>
///     Sending is idempotent on the order id. The outbox retries whenever a
///     reply is lost, and code written against a fake that issued a new id
///     each time would raise duplicate sales orders the first time the real
///     one timed out.
///   </item>
///   <item>
///     A sent order starts in <c>draft</c>, so the poller has a non-terminal
///     state to find and a transition to apply. <see cref="SetState"/> is how
///     a test moves it.
///   </item>
/// </list>
/// </remarks>
public sealed class FakeOdooClient(ILogger<FakeOdooClient> log) : IOdooClient
{
    private readonly Dictionary<string, string> _byOrderId = [];
    private readonly Dictionary<string, string> _states = [];
    private readonly Lock _lock = new();

    /// <summary>Every order this has been handed, in the order it was handed them.</summary>
    public IReadOnlyList<string> Sent
    {
        get { lock (_lock) return [.. _byOrderId.Keys]; }
    }

    public Task<OdooOrderAccepted> SendOrderAsync(string orderId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_byOrderId.TryGetValue(orderId, out var already))
            {
                log.LogInformation("Odoo (fake): {OrderId} was already sent as {OdooId}", orderId, already);
                return Task.FromResult(new OdooOrderAccepted(already));
            }

            var odooId = $"SO{_byOrderId.Count + 1:D5}";
            _byOrderId[orderId] = odooId;
            _states[odooId] = "draft";

            log.LogInformation("Odoo (fake): {OrderId} accepted as {OdooId}", orderId, odooId);
            return Task.FromResult(new OdooOrderAccepted(odooId));
        }
    }

    public Task<IReadOnlyList<OdooOrderState>> GetStatesAsync(
        IReadOnlyList<string> odooOrderIds, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Ids it has never issued are left out rather than reported in
            // some unknown state. A real one answers about what it has.
            IReadOnlyList<OdooOrderState> states =
            [
                .. odooOrderIds
                    .Where(_states.ContainsKey)
                    .Select(id => new OdooOrderState(id, _states[id])),
            ];

            return Task.FromResult(states);
        }
    }

    /// <summary>Moves one order's state, as the real system would over time.</summary>
    public void SetState(string odooOrderId, string state)
    {
        lock (_lock) _states[odooOrderId] = state;
    }
}
