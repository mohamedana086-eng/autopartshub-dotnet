using System.Text.Json;

namespace AutoPartsHub.Api.Mail;

/// <summary>One message. Plain text, until there is a transport that renders HTML.</summary>
public record Email(string To, string Subject, string Body);

/// <summary>Which transport is in play.</summary>
public enum TransportName
{
    /// <summary>Writes to a local outbox rather than sending. Development only.</summary>
    File,

    /// <summary>Nothing implements it, so sending throws. The production default.</summary>
    Unconfigured,
}

/// <summary>
/// Which transport mail goes through, and where the file one writes.
/// </summary>
/// <remarks>
/// The same rule as the other API's <c>lib/mail-transport.ts</c>, and it has to
/// stay the same rule: the two read one <c>MAIL_TRANSPORT</c> variable in one
/// deployment, and a port that decided "unset means send" where the original
/// decided "unset in production means refuse" would be the more dangerous of
/// the two disagreeing quietly.
/// </remarks>
public static class MailTransport
{
    /// <summary>
    /// Which transport is in play, decided from the environment.
    /// </summary>
    /// <remarks>
    /// <c>MAIL_TRANSPORT=file</c> writes to a local outbox rather than sending.
    /// It is the development default and never the production one: a deployment
    /// that silently wrote password-reset links to a file on the server would be
    /// worse than one that sent nothing, because it would look like it worked.
    /// </remarks>
    public static TransportName Name(string? configured, bool isProduction)
    {
        if (configured == "file") return TransportName.File;
        // A name nothing implements. Set deliberately and spelled wrong is not
        // the same as unset, and it must not fall back to writing files.
        if (!string.IsNullOrEmpty(configured)) return TransportName.Unconfigured;

        return isProduction ? TransportName.Unconfigured : TransportName.File;
    }

    /// <summary>The same decision, read from this process's own environment.</summary>
    public static TransportName Name() => Name(
        Environment.GetEnvironmentVariable("MAIL_TRANSPORT"),
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Production",
            StringComparison.OrdinalIgnoreCase));

    /// <summary>Where the file transport writes. One JSON object per line.</summary>
    public static string Outbox => Path.Combine(Directory.GetCurrentDirectory(), ".mail", "outbox.jsonl");
}

public sealed class MailNotConfiguredException()
    : Exception(
        "No email transport is configured, so nothing was sent. Set MAIL_TRANSPORT " +
        "and its credentials — see NOTIF-02.");

/// <summary>
/// Sending email.
/// </summary>
/// <remarks>
/// There is no email provider in this project yet — the backlog calls it
/// NOTIF-02 — and <c>Notification</c> is in-app only: a bell in the header, a
/// read mark, a link. Nothing it writes leaves the building.
///
/// What is deliberately NOT done here is fail at startup when no transport is
/// configured. The live deployment has none, and refusing to boot would take a
/// working shop down to add a feature nobody is using yet. It fails at the
/// point of use instead, loudly in the log, naming the missing variable — so an
/// operator reading the log finds a sentence rather than a silence that looks
/// like a working feature.
/// </remarks>
public sealed class Mailer(ILogger<Mailer> log)
{
    /// <summary>
    /// Sends a message about something that has already happened, and never
    /// fails.
    /// </summary>
    /// <remarks>
    /// The email here is a courtesy about a fact that is already true. The
    /// order was accepted, the ticket was answered, the row is written. Letting
    /// the mailer throw would mean an admin pressing "accept" gets an error
    /// page for an order that WAS accepted — and, worse, tries again.
    ///
    /// Null is accepted and does nothing, so the "is this worth a message at
    /// all" decision can live in one place next to the wording rather than
    /// being a condition at every call site. See <see cref="BusinessMail"/>:
    /// most of what happens to an order is not news, and an internal note on a
    /// ticket is not the customer's mail.
    /// </remarks>
    public async Task NotifyAsync(Email? email, CancellationToken ct = default)
    {
        if (email is null) return;

        try
        {
            await SendAsync(email, ct);
        }
        catch (Exception cause)
        {
            // Named in the log, invisible to the user. An operator reading this
            // finds a sentence; the customer's order went through either way.
            log.LogError(
                cause, "Could not email {To} about \"{Subject}\".", email.To, email.Subject);
        }
    }

    /// <summary>Sends one message, or throws if it cannot.</summary>
    /// <remarks>
    /// Throwing rather than returning false: a caller for whom "the email did
    /// not go" changes what the user should be told needs to know, and a
    /// boolean is the kind of thing that gets ignored at three call sites out
    /// of four. Nothing in this port calls it directly yet — everything that
    /// sends goes through <see cref="NotifyAsync"/>.
    /// </remarks>
    public static async Task SendAsync(Email email, CancellationToken ct = default)
    {
        if (MailTransport.Name() == TransportName.Unconfigured) throw new MailNotConfiguredException();

        var outbox = MailTransport.Outbox;
        Directory.CreateDirectory(Path.GetDirectoryName(outbox)!);

        // The same line the other API appends, field for field and in the same
        // order: one outbox is read by whoever is checking what went out, and a
        // file holding two shapes is a file nobody can grep.
        var line = JsonSerializer.Serialize(new
        {
            at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            to = email.To,
            subject = email.Subject,
            body = email.Body,
        });

        await File.AppendAllTextAsync(outbox, line + "\n", ct);
    }
}
