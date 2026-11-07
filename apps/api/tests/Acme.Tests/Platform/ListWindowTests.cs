using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.WebApi.Controllers;
using Xunit;

namespace Acme.Tests.Platform;

/// <summary>
/// The shared sort and date range, as read from a query string. The database
/// side is covered end to end in <c>ListSortAndDateTests</c>.
/// </summary>
public class ListWindowTests
{
    private static readonly DateTime Jan1 = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan10 = new(2001, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, ListSort.Newest)]
    [InlineData("", ListSort.Newest)]
    [InlineData("newest", ListSort.Newest)]
    [InlineData("oldest", ListSort.Oldest)]
    [InlineData(" OLDEST ", ListSort.Oldest)]
    [InlineData("sideways", ListSort.Newest)]
    public void Sort_words_parse_and_anything_else_is_newest(string? value, ListSort expected) =>
        Assert.Equal(expected, ListWindow.ParseSort(value));

    [Fact]
    public void From_is_inclusive_and_to_is_exclusive()
    {
        var w = ListWindow.From("newest", Jan1, Jan10);
        Assert.True(w.Contains(Jan1));
        Assert.True(w.Contains(Jan10.AddTicks(-1)));
        Assert.False(w.Contains(Jan10));
        Assert.False(w.Contains(Jan1.AddTicks(-1)));
    }

    [Fact]
    public void No_range_contains_everything_and_is_not_a_range()
    {
        var w = ListWindow.From(null, null, null);
        Assert.False(w.HasRange);
        Assert.True(w.Contains(DateTime.MinValue));
        Assert.True(w.Contains(DateTime.MaxValue));
        Assert.Equal(ListWindow.Default, w);
    }

    [Fact]
    public void A_time_with_no_zone_is_taken_as_utc_not_shifted()
    {
        var unspecified = new DateTime(2001, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var utc = ListWindow.Utc(unspecified)!.Value;
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(12, utc.Hour);
    }

    [Theory]
    [InlineData(null, UserSort.NewestFirst)]
    [InlineData("newest", UserSort.NewestFirst)]
    [InlineData("oldest", UserSort.OldestFirst)]
    [InlineData("NameAsc", UserSort.NameAsc)]
    [InlineData("lastlogindesc", UserSort.LastLoginDesc)]
    [InlineData("99", UserSort.NewestFirst)]
    [InlineData("nonsense", UserSort.NewestFirst)]
    public void Admin_user_sort_accepts_the_shared_words_and_the_older_names(string? value, UserSort expected) =>
        Assert.Equal(expected, AdminController.UserSortFrom(value));
}
