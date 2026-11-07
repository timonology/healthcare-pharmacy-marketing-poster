namespace Acme.Application.Videos;

/// <summary>One selectable narration voice.</summary>
public sealed record VoiceOptionDto(string Id, string Label);

/// <summary>
/// What this deployment can narrate with — the contents of the voice picker.
/// </summary>
/// <remarks>
/// <para>
/// Relayed from ai-service, never decided here. Whether narration uses
/// ElevenLabs depends on whether <c>ELEVENLABS_API_KEY</c> is set, and that key
/// belongs to ai-service alone. Reading it on this side as well would be the
/// same fact in two places — and the bug this endpoint exists to fix was
/// precisely that: the browser deciding from a voice id what the key actually
/// decided, so it offered voices the render would ignore and priced them at a
/// tenth of their cost.
/// </para>
/// </remarks>
public sealed record NarrationOptionsDto(
    /// <summary><c>elevenlabs</c> or <c>edge</c>.</summary>
    string Provider,
    IReadOnlyList<VoiceOptionDto> Voices,
    string DefaultVoiceId,

    /// <summary>
    /// What narration adds to one video, in USD. Shown before queueing.
    /// </summary>
    double EstimateUsdPerVideo);

/// <summary>Asks ai-service which voices this deployment can offer.</summary>
public interface INarrationCatalog
{
    /// <summary>
    /// The voice picker's contents, or null when ai-service cannot be reached.
    /// </summary>
    /// <remarks>
    /// Null rather than a guessed default. A guess here means offering a voice
    /// that may be silently swapped — the exact behaviour being removed — so the
    /// caller is expected to offer no choice at all rather than a wrong one.
    /// </remarks>
    Task<NarrationOptionsDto?> GetAsync(string bearerToken, CancellationToken ct);
}
