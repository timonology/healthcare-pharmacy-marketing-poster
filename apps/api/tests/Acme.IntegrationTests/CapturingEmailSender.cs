using Acme.Application.Messaging;

namespace Acme.IntegrationTests;

/// <summary>
/// An <see cref="IEmailSender"/> that keeps what it was asked to send.
/// </summary>
/// <remarks>
/// <para>
/// The mock sender the app ships with reports success and logs a line — which
/// is right for a dev environment and useless for a test, because every
/// assertion about the message would be an assertion about a log string.
/// This keeps the <see cref="EmailMessage"/> itself, so a test can look at the
/// attachments and the HTML the recipient would receive.
/// </para>
/// <para>
/// Registered once for the whole assembly, on <see cref="ApiFactory"/>. The
/// list is therefore shared: a test must <see cref="Clear"/> before the action
/// it intends to inspect, and must not assume it is the only sender. xUnit runs
/// a collection serially, so clearing first is enough to make <see cref="Last"/>
/// unambiguous.
/// </para>
/// </remarks>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = new();
    private readonly object _lock = new();

    /// <summary>Everything sent so far, oldest first.</summary>
    public IReadOnlyList<EmailMessage> Sent
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    /// <summary>The most recent message, or a failure that says none was sent.</summary>
    public EmailMessage Last
    {
        get
        {
            lock (_lock)
            {
                if (_sent.Count == 0)
                    throw new InvalidOperationException(
                        "No email was sent. The campaign either failed before dispatch or " +
                        "took a path that does not send — check the API log for the reason.");
                return _sent[^1];
            }
        }
    }

    public void Clear()
    {
        lock (_lock) _sent.Clear();
    }

    private TaskCompletionSource? _gate;

    /// <summary>
    /// Stops every send from completing until the handle is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For tests about what is true <i>while</i> a campaign is still going out.
    /// Since dispatch moved to a background worker, that window is a real state
    /// the application can be observed in — a campaign that is queued or sending
    /// counts against the monthly plan limit, for instance — and it is a state a
    /// test cannot otherwise land in, because the mock sender is instant.
    /// </para>
    /// <para>
    /// The alternative was to race: create one campaign, then create another
    /// quickly and hope the worker had not finished the first. That test would
    /// have passed against the broken code often enough to be worthless.
    /// </para>
    /// <para>
    /// <b>Dispose it.</b> A worker blocked here holds its campaign, so a held
    /// sender that is never released stalls every campaign test after it — hence
    /// <c>using</c>, and hence the cancellation token being honoured so a
    /// shutting-down host is not stuck either.
    /// </para>
    /// </remarks>
    public IDisposable Hold()
    {
        lock (_lock)
        {
            if (_gate is not null)
                throw new InvalidOperationException(
                    "The sender is already held. Dispose the first handle before taking another.");

            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return new Handle(this);
    }

    private void Release()
    {
        TaskCompletionSource? gate;
        lock (_lock)
        {
            gate = _gate;
            _gate = null;
        }
        gate?.TrySetResult();
    }

    private sealed class Handle(CapturingEmailSender owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }

    public async Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct)
    {
        TaskCompletionSource? gate;
        lock (_lock) gate = _gate;

        // Waited on before the message is recorded, so a held sender has
        // genuinely not sent anything rather than having sent it and paused.
        if (gate is not null) await gate.Task.WaitAsync(ct);

        lock (_lock) _sent.Add(message);

        // Report success for every recipient. A test about the *content* of the
        // message should not also be exercising the delivery-failure path;
        // that belongs in its own test with its own fake.
        return new EmailDispatchResult(message.To.Count, 0, "captured by the test");
    }
}
