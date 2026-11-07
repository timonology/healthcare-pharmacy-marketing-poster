namespace Acme.Application.Templates;

/// <summary>
/// What an uploaded file actually is, from its first bytes.
/// </summary>
/// <remarks>
/// The browser's <c>Content-Type</c> is the file name's extension wearing a
/// hat: a PDF renamed <c>photo.png</c> arrives as <c>image/png</c>. Stored on
/// that word, it would sail through upload and fail a minute later inside the
/// designer, after the pharmacist had written a brief and pressed Generate. So
/// the bytes decide, and a bad file is refused at upload — before generate.
/// </remarks>
public static class ImageSniffer
{
    /// <summary>"image/jpeg", "image/png" or "image/webp", or null for anything else.</summary>
    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";

        if (head.Length >= 8 && head[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";

        // RIFF....WEBP
        if (head.Length >= 12
            && head[..4].SequenceEqual("RIFF"u8)
            && head[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }

    /// <summary>How many leading bytes <see cref="Detect"/> needs.</summary>
    public const int HeaderLength = 12;
}
