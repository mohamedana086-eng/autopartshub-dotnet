using System.Text.Json;
using AutoPartsHub.Api.Admin;

namespace AutoPartsHub.Api.Support;

/// <summary>A ticket as it is raised, once read.</summary>
public record NewTicket(string Subject, string Body, string? OrderId);

/// <summary>
/// What a ticket is allowed to say, and what a message does to its status.
/// </summary>
/// <remarks>
/// No database in it, so both APIs read the same rules and both can be tested
/// without one. They have to agree: both write to the same table.
/// </remarks>
public static class TicketInput
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string Resolved = "resolved";

    /// <summary>The three, in the order the other API lists them.</summary>
    public static readonly string[] Statuses = [Open, Answered, Resolved];

    public static bool IsKnown(string value) => Statuses.Contains(value);

    public const int MaxSubject = 150;
    public const int MaxBody = 5000;

    /// <summary>
    /// What a ticket becomes when somebody writes on it.
    /// </summary>
    /// <remarks>
    /// The status is derived rather than set, which is the whole design. A
    /// field somebody has to remember to change is a field that is wrong by
    /// Wednesday, and the two states that matter — waiting on us, waiting on
    /// them — are consequences of a message arriving rather than judgements
    /// about it.
    ///
    /// The third case is the one worth having a function for. <b>An internal
    /// note changes nothing.</b> Staff writing to each other have not answered
    /// the customer, and moving the ticket to <c>answered</c> would take it off
    /// the queue of things waiting on us on the strength of a conversation the
    /// customer cannot see and did not receive.
    /// </remarks>
    public static string StatusAfterMessage(string current, bool fromStaff, bool internalNote)
    {
        if (internalNote) return current;

        // A customer writing to a resolved ticket reopens it. Closing a
        // conversation is our decision to make and theirs to overturn by
        // continuing it.
        return fromStaff ? Answered : Open;
    }

    private static string Text(JsonElement? element) => JsonValues.AsString(element).Trim();

    public static Validated<NewTicket> ReadNewTicket(JsonElement body)
    {
        var subject = Text(JsonValues.Get(body, "subject"));
        if (subject.Length == 0) return Validators.Fail<NewTicket>("Give it a subject.");
        if (subject.Length > MaxSubject)
        {
            return Validators.Fail<NewTicket>($"Keep the subject under {MaxSubject} characters.");
        }

        var first = ReadMessageBody(body);
        if (!first.Ok) return Validators.Fail<NewTicket>(first.Error!);

        var orderId = Text(JsonValues.Get(body, "orderId"));

        return Validators.Ok(new NewTicket(
            subject, first.Value!, orderId.Length > 0 ? orderId : null));
    }

    public static Validated<string> ReadMessageBody(JsonElement body)
    {
        var message = Text(JsonValues.Get(body, "body"));

        if (message.Length == 0) return Validators.Fail<string>("Say what the problem is.");
        if (message.Length > MaxBody)
        {
            // Refused rather than truncated. A message cut off halfway is a
            // message that says something its author did not, and the half that
            // was dropped is usually the part with the detail in it.
            return Validators.Fail<string>($"Keep a message under {MaxBody} characters.");
        }

        return Validators.Ok(message);
    }

    /// <summary>APH-T-260827-K3F9 — an order reference with a T in it.</summary>
    public static string MakeReference(DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var date = $"{now.Year % 100:D2}{now.Month:D2}{now.Day:D2}";
        var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

        return $"APH-T-{date}-{suffix}";
    }
}
