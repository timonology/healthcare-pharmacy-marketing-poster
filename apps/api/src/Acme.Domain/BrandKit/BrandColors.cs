using System.Text.RegularExpressions;
using Acme.Domain.Common;

namespace Acme.Domain.BrandKit;

/// <summary>Hex colors that drive the brand palette.</summary>
public readonly record struct BrandColors
{
    private static readonly Regex Hex = new(
        @"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$",
        RegexOptions.Compiled);

    public string Primary { get; }
    public string Secondary { get; }
    public string Accent { get; }

    private BrandColors(string primary, string secondary, string accent)
    {
        Primary = primary;
        Secondary = secondary;
        Accent = accent;
    }

    public static BrandColors Create(string primary, string secondary, string accent)
    {
        Validate(primary, nameof(primary));
        Validate(secondary, nameof(secondary));
        Validate(accent, nameof(accent));
        return new BrandColors(primary, secondary, accent);
    }

    private static void Validate(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Hex.IsMatch(value))
            throw new DomainException($"{field} must be a hex color like #1f2937.");
    }
}
