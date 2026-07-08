namespace Acme.Application.Messaging;

public sealed record SmsMessage(
    string FromSenderId,
    IReadOnlyList<string> To,
    string Body);

public sealed record SmsDispatchResult(int Sent, int Failed, string? Note);

public interface ISmsSender
{
    Task<SmsDispatchResult> SendAsync(SmsMessage message, CancellationToken ct);
}
