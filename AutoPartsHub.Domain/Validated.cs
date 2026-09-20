namespace AutoPartsHub.Domain;

/// <summary>
/// A value that was read from a request, or the sentence explaining why it
/// could not be.
/// </summary>
/// <remarks>
/// The error is a sentence rather than a code because it reaches a person in a
/// form. Two APIs that refuse the same thing in different words are two
/// products, so the wording is part of what is being ported and not something
/// each layer phrases for itself.
///
/// It lives here, in the layer with nothing underneath it, because both sides
/// of the application produce one: the admin input readers parse a body into
/// it, and the rules in <see cref="Orders.OrderStatuses"/> and
/// <see cref="Orders.OrderFilters"/> decide with it. It used to sit in the
/// admin validators, which meant the order rules had to reach up into the
/// API to say "no" — the reverse dependency the architecture test now refuses.
/// </remarks>
public record Validated<T>(bool Ok, T? Value, string? Error);

/// <summary>Making one.</summary>
/// <remarks>
/// Two methods rather than two constructors, so that a refusal reads as a
/// refusal at the call site: <c>Validation.Fail&lt;StatusChange&gt;("…")</c>
/// says what it is doing where <c>new(false, default, "…")</c> says what it
/// is made of.
/// </remarks>
public static class Validation
{
    public static Validated<T> Ok<T>(T value) => new(true, value, null);

    public static Validated<T> Fail<T>(string error) => new(false, default, error);
}
