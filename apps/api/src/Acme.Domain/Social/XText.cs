using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Acme.Domain.Social;

/// <summary>
/// X's post length rule: 280, counted X's way rather than in characters.
/// </summary>
/// <remarks>
/// <para>
/// X weighs text (its open-source <c>twitter-text</c> rules): most Latin,
/// Greek, Cyrillic and other scripts below U+1100, plus some punctuation,
/// count 1 each; everything else — Chinese, Japanese, Korean, emoji — counts 2;
/// every link counts 23 however long or short it is. The limit is 280.
/// </para>
/// <para>
/// Counted on the side of caution where X's rules are intricate: any word
/// that looks like a web address (including a bare <c>pharmacy.co.uk</c>) is
/// counted as a link, and an emoji counts 2 whatever it is made of. A post
/// we pass is one X accepts; one we refuse is at worst a few characters
/// short of X's own limit. The web app counts the same way
/// (<c>apps/web/src/lib/x-text.ts</c>) — change both together.
/// </para>
/// <para>
/// X Premium accounts may post more, but nothing tells us which accounts
/// are Premium, so every account is held to the standard limit.
/// </para>
/// </remarks>
public static class XText
{
    public const int MaxWeightedLength = 280;
    public const int LinkLength = 23;

    // Anything shaped like a web address: an optional scheme, labels joined by
    // dots ending in a 2+ letter top-level domain, an optional port and path.
    private static readonly Regex Link = new(
        @"(?<![@A-Za-z0-9_.\-])(?:https?://)?(?:[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?\.)+[a-z]{2,}(?::\d{1,5})?(?:/[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>The text's length as X counts it.</summary>
    public static int WeightedLength(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        string normalized;
        try
        {
            normalized = text.Normalize(NormalizationForm.FormC);
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            normalized = text;   // a stray surrogate: count it as it is
        }

        var total = 0;
        var last = 0;
        foreach (Match m in Link.Matches(normalized))
        {
            total += Plain(normalized[last..m.Index]) + LinkLength;
            last = m.Index + m.Length;
        }
        return total + Plain(normalized[last..]);
    }

    /// <summary>True when X will take this text as a post.</summary>
    public static bool Fits(string? text) => WeightedLength(text) <= MaxWeightedLength;

    private static int Plain(string text)
    {
        var total = 0;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var element = (string)e.Current;
            if (IsEmoji(element))
            {
                total += 2;
                continue;
            }
            foreach (var cp in CodePoints(element))
                total += Weight(cp);
        }
        return total;
    }

    private static int Weight(int cp) =>
        cp is <= 0x10FF
            or >= 0x2000 and <= 0x200D
            or >= 0x2010 and <= 0x201F
            or >= 0x2032 and <= 0x2037
            ? 1
            : 2;

    private static bool IsEmoji(string element)
    {
        foreach (var cp in CodePoints(element))
        {
            if (cp is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or 0xFE0F or 0x200D)
                return true;
        }
        return false;
    }

    /// <summary>Code points; a stray surrogate (malformed text) counts as itself.</summary>
    private static IEnumerable<int> CodePoints(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else
            {
                yield return s[i];
            }
        }
    }
}
