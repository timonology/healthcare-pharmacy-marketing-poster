namespace Acme.Application.Common;

/// <summary>
/// Runs once at application startup. Used for things like Mongo index
/// creation that need a live connection but shouldn't run per-request.
/// </summary>
public interface IStartupTask
{
    Task ExecuteAsync(CancellationToken ct);
}
