using Acme.Application.Admin;
using Xunit;

namespace Acme.Tests.Admin;

/// <summary>
/// Tests for the masking that stands between an administrator and a
/// pharmacy's patient list.
/// </summary>
/// <remarks>
/// Most of these are not "does it format nicely" — they are "does it ever
/// fail open". A masking function has one catastrophic failure mode, which is
/// returning something close enough to the input to be useful, and it usually
/// arrives through an input shape nobody thought about. So the awkward inputs
/// get more attention here than the happy path does.
/// </remarks>
public class ContactMaskTests
{
    /* ---------------- Email ---------------- */

    [Fact]
    public void Email_keeps_first_character_and_domain()
    {
        Assert.Equal("j•••••@gmail.com", ContactMask.Email("jane.doe@gmail.com"));
    }

    [Fact]
    public void Email_returns_null_for_nothing()
    {
        Assert.Null(ContactMask.Email(null));
        Assert.Null(ContactMask.Email(""));
        Assert.Null(ContactMask.Email("   "));
    }

    [Theory]
    [InlineData("ab@x.com")]   // two-character local part
    [InlineData("a@x.com")]    // one character
    public void Email_hides_short_local_parts_entirely(string input)
    {
        var masked = ContactMask.Email(input);

        Assert.NotNull(masked);
        Assert.StartsWith("•", masked);
    }

    [Theory]
    [InlineData("notanemail")]
    [InlineData("@gmail.com")]
    [InlineData("jane@")]
    [InlineData("@")]
    public void Email_masks_malformed_input_completely(string input)
    {
        // No '@' to anchor on, or nothing either side of it. The rule is that
        // an unrecognised shape reveals nothing at all, rather than falling
        // through to the input.
        Assert.Equal("•••••", ContactMask.Email(input));
    }

    [Fact]
    public void Email_splits_on_the_last_at_sign()
    {
        // A quoted local part may contain '@'. Splitting on the first one puts
        // part of the local part into what we then publish as the domain.
        var masked = ContactMask.Email("\"odd@name\"@example.com");

        Assert.Equal("\"•••••@example.com", masked);
        Assert.DoesNotContain("odd", masked);
    }

    [Fact]
    public void Email_is_trimmed_before_masking()
    {
        Assert.Equal("j•••••@gmail.com", ContactMask.Email("  jane@gmail.com  "));
    }

    /* ---------------- Phone ---------------- */

    [Fact]
    public void Phone_keeps_the_last_three_digits()
    {
        Assert.Equal("•••••123", ContactMask.Phone("+447700900123"));
    }

    [Fact]
    public void Phone_ignores_formatting()
    {
        // Same number, four renderings. All must mask identically, or the
        // formatting itself becomes a channel for distinguishing records.
        Assert.Equal("•••••123", ContactMask.Phone("+44 7700 900123"));
        Assert.Equal("•••••123", ContactMask.Phone("(07700) 900-123"));
        Assert.Equal("•••••123", ContactMask.Phone("07700900123"));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("999")]
    [InlineData("abc")]
    public void Phone_hides_short_numbers_entirely(string input)
    {
        Assert.Equal("•••••", ContactMask.Phone(input));
    }

    [Fact]
    public void Phone_returns_null_for_nothing()
    {
        Assert.Null(ContactMask.Phone(null));
        Assert.Null(ContactMask.Phone(""));
    }

    /* ---------------- Recipient ---------------- */

    [Fact]
    public void Recipient_picks_the_right_mask_for_the_channel()
    {
        Assert.Equal("j•••••@gmail.com", ContactMask.Recipient("jane@gmail.com"));
        Assert.Equal("•••••123", ContactMask.Recipient("+447700900123"));
    }

    [Fact]
    public void Recipient_never_returns_null_or_empty()
    {
        // A delivery report row always renders something. A blank cell reads
        // as a broken table rather than as a redaction.
        Assert.Equal("•••••", ContactMask.Recipient(null));
        Assert.Equal("•••••", ContactMask.Recipient(""));
        Assert.Equal("•••••", ContactMask.Recipient("   "));
    }

    /* ---------------- The property that actually matters ---------------- */

    [Theory]
    [InlineData("jane.doe@gmail.com")]
    [InlineData("a@b.co")]
    [InlineData("notanemail")]
    [InlineData("+447700900123")]
    [InlineData("07700 900 123")]
    [InlineData("x")]
    [InlineData("first.last+tag@subdomain.example.co.uk")]
    public void Nothing_ever_passes_through_unmasked(string input)
    {
        var email = ContactMask.Email(input);
        var phone = ContactMask.Phone(input);
        var recipient = ContactMask.Recipient(input);

        // The single regression this whole file exists to prevent: a code path
        // that hits an input it doesn't recognise and returns it verbatim.
        Assert.NotEqual(input, email);
        Assert.NotEqual(input, phone);
        Assert.NotEqual(input, recipient);
    }

    [Theory]
    [InlineData("jane.doe@gmail.com", "ane.doe")]
    [InlineData("christopher@nhs.net", "hristopher")]
    [InlineData("first.last+tag@example.com", "irst.last+tag")]
    public void At_most_one_character_of_the_local_part_survives(
        string input, string mustNotAppear)
    {
        var masked = ContactMask.Email(input);

        Assert.NotNull(masked);
        Assert.DoesNotContain(mustNotAppear, masked);
    }

    [Fact]
    public void Masked_phone_reveals_no_more_than_three_digits()
    {
        var masked = ContactMask.Phone("+447700900123");

        Assert.NotNull(masked);
        Assert.Equal(3, masked!.Count(char.IsDigit));
    }
}
