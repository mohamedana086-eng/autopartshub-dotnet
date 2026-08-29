using AutoPartsHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoPartsHub.Api.Auth;

/// <summary>
/// Gate for <c>/api/supplier/**</c> — the portal a supplier signs into.
/// </summary>
/// <remarks>
/// Three separate questions, and all three have to be yes:
///
/// <list type="number">
///   <item>signed in</item>
///   <item>a SUPPLIER account, and one attached to a supplier</item>
///   <item>that supplier is trading</item>
/// </list>
///
/// The third is the one worth spelling out. A supplier who has signed up and
/// not been approved, and one who has been stopped, are the same state in this
/// schema — <c>Supplier.active</c> is false for both, deliberately, because
/// nothing of theirs is for sale in either case. It follows that neither
/// should be able to read the portal: an applicant nobody has approved would
/// otherwise see the warehouse's stock of parts that happen to share their
/// supplier row, and a supplier who was stopped this morning would keep the
/// view they had yesterday.
///
/// Not a singleton like <see cref="AdminGate"/>, because it reads the database.
/// </remarks>
public sealed class SupplierGate(SessionTokens tokens, AutoPartsContext db)
{
    public async Task<SupplierGateResult> Require(HttpContext http, CancellationToken ct = default)
    {
        var session = tokens.Decode(http.Request.Cookies[SessionTokens.CookieName]);

        if (session is null)
        {
            return SupplierGateResult.Refused(
                Results.Json(new { error = "Not signed in." }, statusCode: 401));
        }
        if (session.Role != Roles.SupplierRole)
        {
            return SupplierGateResult.Refused(
                Results.Json(new { error = "Supplier access required." }, statusCode: 403));
        }

        // Read fresh on every request, not carried in the session. The cookie
        // already holds a role and a pricing tier that go stale until the next
        // sign-in, and a third stale field would be the one deciding whether
        // somebody stopped last week can still read the stock. Approval and
        // suspension have to take effect at once, so they are read at once.
        var attached = await db.Clients
            .Where(c => c.Id == session.UserId && c.SupplierId != null)
            .Select(c => new
            {
                SupplierId = c.Supplier!.Id,
                SupplierName = c.Supplier.Name,
                c.Supplier.Active,
                Approved = c.Supplier.ApprovedAt != null,
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        // A SUPPLIER account with no supplier row is not a state the sign-up
        // can produce — it writes both in one transaction — but it is one an
        // admin could produce by hand, and the portal has nothing to scope
        // itself to.
        if (attached is null)
        {
            return SupplierGateResult.Refused(Results.Json(
                new { error = "This account is not attached to a supplier." }, statusCode: 403));
        }

        if (!attached.Active)
        {
            return SupplierGateResult.Refused(Results.Json(
                new
                {
                    // The two states are told apart HERE and nowhere else,
                    // because this is the one place the difference is
                    // actionable: an applicant is waiting on us, and a stopped
                    // supplier needs to talk to somebody.
                    error = attached.Approved
                        ? "This supplier account has been stopped. Get in touch with us to reopen it."
                        : "This application has not been approved yet.",
                },
                statusCode: 403));
        }

        return SupplierGateResult.Allowed(attached.SupplierId, attached.SupplierName);
    }
}

public record SupplierGateResult(bool Ok, string? SupplierId, string? SupplierName, IResult? Response)
{
    public static SupplierGateResult Allowed(string supplierId, string supplierName) =>
        new(true, supplierId, supplierName, null);

    public static SupplierGateResult Refused(IResult response) => new(false, null, null, response);
}
