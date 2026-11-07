namespace Acme.Application.Billing;

/// <summary>
/// Idempotency ledger for Stripe webhooks.
///
/// Stripe guarantees *at-least-once* delivery, not exactly-once: any event can
/// arrive twice — on retry after a timeout, after a 500, or because a network
/// blip lost our 200. Without a ledger, a duplicated
/// <c>checkout.session.completed</c> for a credit pack grants the credits
/// twice. Recording the event id before acting makes replays free.
/// </summary>
public interface IProcessedWebhookRepository
{
    /// <summary>
    /// Atomically claims an event id. Returns true if this caller won the race
    /// and should process the event; false if it was already handled.
    /// </summary>
    Task<bool> TryMarkProcessedAsync(string eventId, string eventType, CancellationToken ct);
}
