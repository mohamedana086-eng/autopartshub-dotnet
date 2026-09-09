using AutoPartsHub.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AutoPartsHub.Infrastructure.Local;

/// <summary>
/// Writes what it would have sent to the log, and sends nothing.
/// </summary>
/// <remarks>
/// So that the API and the worker run with no SMTP server anywhere near them,
/// which is the acceptance criterion for the development adapters.
///
/// It logs the recipient and the subject and NOT the body. A password-reset
/// body carries a working token, and a log is the least protected place in a
/// deployment — the transport that writes bodies to disk is the file outbox,
/// which is deliberate, local, and gitignored.
/// </remarks>
public sealed class NoopEmailSender(ILogger<NoopEmailSender> log) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        log.LogInformation("Mail not sent (no transport configured): {Subject} -> {To}", subject, to);
        return Task.CompletedTask;
    }
}
