using System.Text.RegularExpressions;
using AutoPartsHub.Api.Mail;
using AutoPartsHub.Api.Orders;

namespace AutoPartsHub.Tests;

/// <summary>
/// Telling a customer what happened.
/// </summary>
/// <remarks>
/// Two things are pinned down. Which events are worth a message — because
/// emailing everything teaches people to ignore the ones that matter — and the
/// one event that must produce no message at all.
///
/// The wording is asserted, not just the presence of a message. These reach a
/// customer, and the other API sends the same sentences to the same people: two
/// systems telling one customer the same thing in different words are two
/// shops.
/// </remarks>
public partial class BusinessMailTests
{
    private static readonly MailContext Ctx = new("https://shop.example");

    private static OrderMail Order(
        string status = "accepted",
        string? reason = null,
        string? trackingNumber = null,
        string? carrier = null) =>
        new("buyer@example.com", "Mohamed", "APH-260827-K3F9", status,
            reason, trackingNumber, carrier);

    /* ---------------------------- which statuses are worth a message --- */

    [Fact]
    public void WritesForTheFourACustomerWouldWantToHearAbout()
    {
        foreach (var status in new[] { "accepted", "rejected", "cancelled", "shipped" })
        {
            var mail = BusinessMail.OrderStatusEmail(Order(status, reason: "out of stock"), Ctx);

            Assert.NotNull(mail);
            Assert.Equal("buyer@example.com", mail!.To);
            Assert.Contains("APH-260827-K3F9", mail.Subject);
        }
    }

    [Fact]
    public void SaysNothingForTheOnesThatAreNotNews()
    {
        // `processing` is us picking it, `delivered` is our record of a box
        // arriving that the customer watched arrive, and `paid` is an
        // accounting fact. Mail about things that are not news is mail nobody
        // reads when it is.
        foreach (var status in new[] { "order_is_sent", "processing", "delivered", "paid" })
        {
            Assert.Null(BusinessMail.OrderStatusEmail(Order(status), Ctx));
        }
    }

    [Fact]
    public void CoversEveryStatusTheLifecycleHasOneWayOrTheOther()
    {
        // A status added later with no decision made about it would silently
        // fall through to null. This does not stop that — it makes it visible
        // here, and in the same assertion the other API makes.
        var sends = OrderStatuses.All
            .Where(status => BusinessMail.OrderStatusEmail(Order(status), Ctx) is not null)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "accepted", "cancelled", "rejected", "shipped" }, sends);
    }

    /* ------------------------------------------- what the message says --- */

    [Fact]
    public void GivesTheReasonWhenAnOrderIsRefused()
    {
        // An order refused with no reason given is a customer telephoning to
        // ask, which is the call this exists to save both sides.
        var mail = BusinessMail.OrderStatusEmail(
            Order("rejected", reason: "Account on stop."), Ctx)!;

        Assert.Contains("Account on stop.", mail.Body);
    }

    [Fact]
    public void SaysSoPlainlyWhenThereIsNoReasonOnTheRow()
    {
        var mail = BusinessMail.OrderStatusEmail(Order("cancelled"), Ctx)!;

        Assert.Contains("not given", mail.Body);
    }

    [Fact]
    public void NamesTheCarrierAndNumberWhenTheShipmentHasThem()
    {
        var mail = BusinessMail.OrderStatusEmail(
            Order("shipped", trackingNumber: "JD01", carrier: "DHL"), Ctx)!;

        Assert.Contains("DHL JD01", mail.Body);
    }

    [Fact]
    public void LeavesTheTrackingLineOutEntirelyWhenThereIsNone()
    {
        // "Tracking: none" reads as a service that failed rather than one that
        // was not offered for this shipment.
        var mail = BusinessMail.OrderStatusEmail(Order("shipped"), Ctx)!;

        Assert.DoesNotContain("Tracking", mail.Body);
    }

    [Fact]
    public void PointsAtTheSiteItWasSentFrom()
    {
        Assert.Contains(
            "https://shop.example/orders", BusinessMail.OrderStatusEmail(Order(), Ctx)!.Body);
    }

    /* ------------------------------------------------- the one that matters --- */

    private static TicketMail Ticket(bool @internal = true, string body = "supplier says three weeks") =>
        new("buyer@example.com", "Mohamed", "APH-T-260827-K3F9", "Wrong part", body, @internal);

    [Fact]
    public void AnInternalNoteProducesNoMessageAtAll()
    {
        // Every other guard on TicketMessage.internal is on the READ path — two
        // readers, a WHERE rather than a filter, the flag not offered on the
        // customer's side. Email is a second door out of the same room, and
        // none of those guards is standing in it.
        Assert.Null(BusinessMail.TicketReplyEmail(Ticket(), Ctx));
    }

    [Fact]
    public void ProducesOneForAnOrdinaryReply()
    {
        var mail = BusinessMail.TicketReplyEmail(
            Ticket(@internal: false, body: "On its way"), Ctx);

        Assert.NotNull(mail);
        Assert.Contains("APH-T-260827-K3F9", mail!.Subject);
        Assert.Contains("On its way", mail.Body);
    }

    /* ------------------------------------------------------ the wiring --- */

    private static string ApiSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AutoPartsHub.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return File.ReadAllText(Path.Combine(dir!.FullName, "AutoPartsHub.Api", relative));
    }

    /// <summary>An endpoint deciding for itself whether a note is mailable.</summary>
    [GeneratedRegex(@"if\s*\(\s*!?\s*internalNote[^)]*\)[\s\S]{0,120}NotifyAsync")]
    private static partial Regex BranchesOnTheNote();

    /// <summary>The flag handed to the builder rather than acted on first.</summary>
    [GeneratedRegex(@"new TicketMail\([\s\S]{0,400}?internalNote\)")]
    private static partial Regex PassesTheNoteThrough();

    [Fact]
    public void TheNoteIsCheckedInTheBuilderNotAtTheCallSite()
    {
        // One place, next to the wording, rather than a condition somebody has
        // to remember at every endpoint that sends. Asserted over the source
        // because the alternative — an endpoint that forgot — is exactly what
        // this prevents.
        var source = ApiSource("Endpoints/TicketEndpoints.cs");

        // The endpoint hands the flag straight to the builder and does not
        // branch on it: a branch here would be a second place for the rule to
        // live, and the two would eventually disagree.
        Assert.Matches(PassesTheNoteThrough(), source);
        Assert.DoesNotMatch(BranchesOnTheNote(), source);
    }

    [Fact]
    public void SendingGoesThroughNotifyWhichSwallowsRatherThanSendWhichThrows()
    {
        // SendAsync throws on purpose — a recovery link that did not arrive is
        // a recovery that did not happen. These messages are courtesies about
        // facts already true, and an admin who pressed "accept" must not get an
        // error page for an order that WAS accepted: they would press it again.
        foreach (var file in new[]
                 {
                     "Endpoints/AdminOrderWriteEndpoints.cs",
                     "Endpoints/TicketEndpoints.cs",
                 })
        {
            var source = ApiSource(file);

            Assert.Contains("NotifyAsync(", source);
            Assert.DoesNotContain("Mailer.SendAsync(", source);
        }
    }

    /* --------------------------------------------------- the transport --- */

    [Fact]
    public void UnsetInProductionRefusesRatherThanWritingAFile()
    {
        // A deployment that silently wrote password-reset links to a file on
        // the server would be worse than one that sent nothing, because it
        // would look like it worked. The same rule as the other API's, because
        // the two read one MAIL_TRANSPORT variable in one deployment.
        Assert.Equal(TransportName.Unconfigured, MailTransport.Name(null, isProduction: true));
        Assert.Equal(TransportName.File, MailTransport.Name(null, isProduction: false));
        Assert.Equal(TransportName.File, MailTransport.Name("file", isProduction: true));
    }

    [Fact]
    public void ANameNothingImplementsIsNotAFallback()
    {
        // Set deliberately and spelled wrong is not the same as unset. Falling
        // back to the file transport there would turn a typo into a deployment
        // that looks like it is sending.
        Assert.Equal(TransportName.Unconfigured, MailTransport.Name("smtp", isProduction: false));
        Assert.Equal(TransportName.Unconfigured, MailTransport.Name("FILE", isProduction: false));
    }
}
