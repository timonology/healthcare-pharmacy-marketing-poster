using Acme.Application.Common;
using Acme.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Messaging;

public sealed class MockSmsSender(ILogger<MockSmsSender> logger) : ISmsSender
{
    public Task<SmsDispatchResult> SendAsync(SmsMessage message, CancellationToken ct)
    {
        logger.LogInformation(
            "[MOCK sms] from={From} to={To} body={Body}",
            message.FromSenderId,
            string.Join(", ", message.To.Select(PersonalData.MaskPhone)),
            message.Body);
        return Task.FromResult(new SmsDispatchResult(message.To.Count, 0, "Mock SMS — no message actually sent."));
    }
}
