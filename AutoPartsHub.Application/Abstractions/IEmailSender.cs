namespace AutoPartsHub.Application.Abstractions;

/// <summary>
/// Sending one message, however it is sent.
/// </summary>
/// <remarks>
/// Primitives rather than a message record, so that the transport and the
/// thing that composes a message do not have to share a type. The composing is
/// somebody else's job — <c>BusinessMail</c> already does it — and an
/// interface that took its output would make every future transport depend on
/// how today's templates happen to be shaped.
///
/// Sending must not be able to fail a request. The notification work (T-193)
/// puts the event in an outbox inside the transaction and lets the worker do
/// this part, so a slow SMTP server delays a message rather than losing an
/// order. Implementations may therefore be slow; they may not throw for
/// anything a caller is expected to handle.
/// </remarks>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
