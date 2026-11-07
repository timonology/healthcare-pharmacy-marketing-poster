using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The public template gallery: the house set leads, everything else follows,
/// and paging across the boundary between the two loses nothing.
/// </summary>
public sealed class TemplateGalleryTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Page(List<Item> Items, int Total);
    private sealed record Item(string Id);

    private async Task<Page> GetAsync(HttpClient client, string query)
    {
        using var res = await client.GetAsync($"/api/templates?{query}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<Page>(Json))!;
    }

    [Fact]
    public async Task The_house_set_comes_first_for_a_visitor()
    {
        RequireDatabase();
        var page = await GetAsync(Api.NewClient(), "take=100");

        var ids = page.Items.Select(i => i.Id).ToList();
        ids.Should().Contain(id => id.StartsWith("sys-house-"), "the house set is seeded at startup");

        var firstOther = ids.FindIndex(id => !id.StartsWith("sys-house-"));
        if (firstOther >= 0)
            ids.Skip(firstOther).Should().NotContain(id => id.StartsWith("sys-house-"),
                "no house template may appear after any other template");
    }

    [Fact]
    public async Task Paging_across_the_house_boundary_is_exact()
    {
        RequireDatabase();
        var client = Api.NewClient();
        var all = (await GetAsync(client, "take=100")).Items.Select(i => i.Id).ToList();

        var paged = new List<string>();
        for (var skip = 0; skip < all.Count; skip += 7)
            paged.AddRange((await GetAsync(client, $"skip={skip}&take=7")).Items.Select(i => i.Id));

        paged.Should().Equal(all, "pages of seven, stitched together, are the same list");
    }

    [Theory]
    [InlineData("skip=-5&take=10")]
    [InlineData("take=0")]
    [InlineData("take=-1")]
    [InlineData("take=100000")]
    public async Task Out_of_range_paging_is_clamped_not_an_error(string query)
    {
        RequireDatabase();
        var page = await GetAsync(Api.NewClient(), query);
        page.Items.Count.Should().BeInRange(1, 100);
    }
}
