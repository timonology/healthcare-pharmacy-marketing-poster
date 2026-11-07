namespace Acme.Infrastructure.Options;

/// <summary>Connection settings for the Python ai-service.</summary>
/// <remarks>
/// Most ai-service work (video rendering) is dispatched through Mongo rather
/// than HTTP — the worker polls a collection. Template generation is different:
/// an administrator is sitting in front of the screen waiting for a result, so
/// it is a synchronous request/response call and needs a base URL.
/// </remarks>
public sealed class AiServiceOptions
{
    public const string SectionName = "AiService";

    public string? BaseUrl { get; set; }

    /// <summary>
    /// Generation runs a large model and can take a repair round or two, so the
    /// default HttpClient timeout of 100s is not enough. This matches the
    /// 180s ceiling the generator chain sets on its own model calls, plus room
    /// for the request itself.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 210;
}
