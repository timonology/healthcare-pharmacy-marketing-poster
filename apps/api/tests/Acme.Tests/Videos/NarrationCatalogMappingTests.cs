using Acme.Application.Videos;
using Acme.Infrastructure.VideoPipeline;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// What the API makes of what ai-service sent about narration.
/// </summary>
/// <remarks>
/// <para>
/// The voice list decides two things a pharmacist sees before spending money:
/// which voices are offered, and what a render is quoted at. Both were wrong
/// until recently — the dialog offered Edge voices on deployments that would
/// silently swap them for ElevenLabs, and priced those renders at a tenth of
/// their cost — so the mapping that produces it is worth pinning.
/// </para>
/// <para>
/// In particular the first test below. That rule was written the wrong way
/// round (<c>wire.Provider ?? "edge"</c>), caught by reading rather than by
/// running, and is exactly the sort of cheap-looking default that reintroduces
/// an under-quote.
/// </para>
/// </remarks>
public class NarrationCatalogMappingTests
{
    private static List<VoiceOptionDto> TwoVoices => new()
    {
        new VoiceOptionDto("11labs:21m00Tcm4TlvDq8ikWAM", "Rachel"),
        new VoiceOptionDto("11labs:AZnzlk1XvdvUeBnXmlld", "Domi"),
    };

    [Fact]
    public void A_complete_response_maps_across()
    {
        var mapped = AiNarrationCatalog.Map(
            new AiNarrationCatalog.WireResponse("elevenlabs", TwoVoices, "11labs:AZnzlk1XvdvUeBnXmlld", 0.05));

        mapped.Should().NotBeNull();
        mapped!.Provider.Should().Be("elevenlabs");
        mapped.Voices.Should().HaveCount(2);
        mapped.DefaultVoiceId.Should().Be("11labs:AZnzlk1XvdvUeBnXmlld");
        mapped.EstimateUsdPerVideo.Should().Be(0.05);
    }

    [Fact]
    public void A_missing_provider_is_refused_rather_than_assumed_to_be_edge()
    {
        // The whole reason this file exists. "edge" is the free-looking answer:
        // it would label paid voices as free and quote £0 for a render that
        // costs about $0.05.
        AiNarrationCatalog.Map(
            new AiNarrationCatalog.WireResponse(null, TwoVoices, "11labs:x", 0.05))
            .Should().BeNull();

        AiNarrationCatalog.Map(
            new AiNarrationCatalog.WireResponse("   ", TwoVoices, "11labs:x", 0.05))
            .Should().BeNull();
    }

    [Fact]
    public void No_voices_means_no_options_rather_than_an_empty_picker()
    {
        // An empty dropdown is worse than none: it looks like a broken control
        // instead of a deployment that has not been told which voices to offer.
        AiNarrationCatalog.Map(
            new AiNarrationCatalog.WireResponse("elevenlabs", new List<VoiceOptionDto>(), "11labs:x", 0.05))
            .Should().BeNull();

        AiNarrationCatalog.Map(
            new AiNarrationCatalog.WireResponse("elevenlabs", null, "11labs:x", 0.05))
            .Should().BeNull();

        AiNarrationCatalog.Map(null).Should().BeNull();
    }

    [Fact]
    public void A_missing_default_falls_back_to_the_first_voice_offered()
    {
        // So the dialog always opens with something selected. Opening with an
        // empty selection sends no voiceId, which works — but it looks like the
        // control failed to load.
        var mapped = AiNarrationCatalog.Map(
            new AiNarrationCatalog.WireResponse("elevenlabs", TwoVoices, null, 0.05));

        mapped!.DefaultVoiceId.Should().Be("11labs:21m00Tcm4TlvDq8ikWAM");
    }

    [Fact]
    public void The_default_is_always_one_of_the_voices_on_offer()
    {
        // The property the dialog depends on. A default that is not in the list
        // renders as a select with nothing chosen.
        foreach (var wire in new[]
        {
            new AiNarrationCatalog.WireResponse("elevenlabs", TwoVoices, null, 0.05),
            new AiNarrationCatalog.WireResponse("elevenlabs", TwoVoices, "", 0.05),
            new AiNarrationCatalog.WireResponse("edge",
                new List<VoiceOptionDto> { new("en-US-JennyNeural", "Jenny") }, null, 0),
        })
        {
            var mapped = AiNarrationCatalog.Map(wire);
            mapped.Should().NotBeNull();

            // Read out before the lambda: the not-null state of `mapped` does
            // not flow into a closure, so `mapped.DefaultVoiceId` in there is a
            // nullable dereference.
            var chosen = mapped!.DefaultVoiceId;
            mapped.Voices.Should().Contain(v => v.Id == chosen);
        }
    }
}
