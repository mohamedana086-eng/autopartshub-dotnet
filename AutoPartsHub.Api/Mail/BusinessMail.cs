namespace AutoPartsHub.Api.Mail;

/// <summary>Where a customer reads the rest. Passed in so this stays pure.</summary>
public record MailContext(string SiteUrl);

/// <param name="Reason">Why it was refused or called off. Present exactly when those happened.</param>
public record OrderMail(
    string To, string Name, string Reference, string Status,
    string? Reason, string? TrackingNumber, string? Carrier);

/// <param name="Body">What was written, so the customer can answer without a login.</param>
/// <param name="Internal">Whether this was a note staff wrote to each other.</param>
public record TicketMail(
    string To, string Name, string Reference, string Subject, string Body, bool Internal);

/// <summary>
/// Telling a customer what happened.
/// </summary>
/// <remarks>
/// Until now the only email this project sent was about the account itself —
/// confirm your address, reset your password. Nothing went out when an order
/// was accepted, refused, shipped or called off, and nothing went out when
/// somebody answered a ticket. A customer who could not see the shop's screens
/// had no way to learn any of it.
///
/// Pure, and separate from the transport: these functions build a message or
/// decide there is none to build, and both halves are worth testing directly.
/// The sending, and the swallowing of its failures, is <see cref="Mailer"/>.
///
/// <b>NULL IS A REAL ANSWER HERE.</b> Most of what happens to an order is not
/// news. <c>processing</c> is us picking it, <c>delivered</c> is our record of a
/// box arriving that the customer watched arrive, and <c>paid</c> is an
/// accounting fact. Emailing all of them would teach people to ignore the ones
/// that matter, which costs more than the silence does — so the statuses worth
/// a message are a short list, and everything else returns null.
///
/// Every sentence is copied from the other API rather than rewritten. These
/// reach a customer, and two systems telling the same customer the same thing
/// in different words are two shops.
/// </remarks>
public static class BusinessMail
{
    /// <summary>
    /// The four statuses a customer should hear about, and what to say.
    /// </summary>
    /// <remarks>
    /// Written as one switch rather than a chain of ifs, because the
    /// interesting property is which statuses are NOT in it — and a chain of
    /// ifs makes an absence invisible.
    /// </remarks>
    public static Email? OrderStatusEmail(OrderMail order, MailContext ctx)
    {
        var link = $"{ctx.SiteUrl}/orders";
        string Sign(string lines) =>
            $"Hello {order.Name},\n\n{lines}\n\nYou can see it at {link}\n";

        switch (order.Status)
        {
            case "accepted":
                return new Email(
                    order.To,
                    $"We are supplying order {order.Reference}",
                    Sign(
                        $"We have accepted order {order.Reference} and are getting it ready. " +
                        "We will write again when it goes out."));

            case "rejected":
                return new Email(
                    order.To,
                    $"We cannot supply order {order.Reference}",
                    Sign(
                        $"We are sorry — we cannot supply order {order.Reference}.\n\n" +
                        // The reason is the message. An order refused with no
                        // reason given is a customer telephoning to ask, which
                        // is the call this exists to save both sides.
                        $"Reason: {order.Reason ?? "not given"}\n\n" +
                        "Nothing has been charged. Reply to this message if you would like " +
                        "us to look for an alternative."));

            case "cancelled":
                return new Email(
                    order.To,
                    $"Order {order.Reference} has been cancelled",
                    Sign(
                        $"Order {order.Reference} has been cancelled.\n\n" +
                        $"Reason: {order.Reason ?? "not given"}\n\n" +
                        "Nothing has been charged."));

            case "shipped":
                return new Email(
                    order.To,
                    $"Order {order.Reference} is on its way",
                    Sign(
                        $"Order {order.Reference} has left us." +
                        // Named only when there is one. "Tracking: none" is
                        // worse than no line at all: it reads as a service that
                        // failed rather than one not offered for this shipment.
                        (order.TrackingNumber is { Length: > 0 }
                            ? "\n\nTracking: " +
                              (order.Carrier is { Length: > 0 } ? $"{order.Carrier} " : "") +
                              order.TrackingNumber
                            : "")));

            default:
                // processing, delivered, paid, order_is_sent. See the note
                // above: not news, and mail about things that are not news is
                // mail nobody reads when it is.
                return null;
        }
    }

    /// <summary>
    /// A reply on a ticket.
    /// </summary>
    /// <remarks>
    /// <b>THE ONE LINE THAT MATTERS IS THE FIRST.</b> An internal note produces
    /// NO EMAIL. The whole ticket design keeps notes away from the customer —
    /// two readers, a WHERE rather than a filter, the flag not offered on their
    /// side — and every one of those guards is on the READ path. Email is a
    /// second door out of the same room, and a mailer that did not check this
    /// column would walk the note straight through it, to the person it was
    /// written about, with no screen involved and nothing to take back.
    /// </remarks>
    public static Email? TicketReplyEmail(TicketMail ticket, MailContext ctx)
    {
        if (ticket.Internal) return null;

        return new Email(
            ticket.To,
            $"Re: {ticket.Subject} ({ticket.Reference})",
            $"Hello {ticket.Name},\n\n" +
            $"We have replied about \"{ticket.Subject}\":\n\n" +
            $"{ticket.Body}\n\n" +
            $"You can answer at {ctx.SiteUrl}/help\n");
    }

    /// <summary>
    /// Where this site is, for links that have to survive leaving it.
    /// </summary>
    /// <remarks>
    /// Every link the app renders is site-relative, which is right — until one
    /// goes in an email, where there is no page for it to be relative to.
    /// <c>SITE_URL</c> is that one exception, and it is read here rather than in
    /// each caller so a deployment that forgets it fails the same way
    /// everywhere.
    ///
    /// The fallback is localhost rather than a guess at the production host: a
    /// link pointing at a developer's own machine is obviously wrong to whoever
    /// receives it, where one pointing at a plausible but wrong domain is not.
    /// </remarks>
    public static MailContext Context()
    {
        var configured = Environment.GetEnvironmentVariable("SITE_URL")?.Trim();
        var url = string.IsNullOrEmpty(configured) ? "http://localhost:3000" : configured;

        return new MailContext(url.TrimEnd('/'));
    }
}
