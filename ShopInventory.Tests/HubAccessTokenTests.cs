using Microsoft.AspNetCore.Http;
using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Pins where a bearer token may arrive in the query string instead of a header.
/// </summary>
/// <remarks>
/// NotificationHub is <c>[Authorize]</c>, and a WebSocket handshake carries no headers a browser can
/// set, so SignalR sends <c>?access_token=</c>. Without this the hub can only ever authenticate the
/// clients that can set a header.
///
/// The path check is the security half: a token in a query string ends up in proxy logs and browser
/// history, so it is read for the hub paths and nowhere else.
/// </remarks>
public sealed class HubAccessTokenTests
{
    private static IQueryCollection Query(string? accessToken) =>
        accessToken is null
            ? new QueryCollection()
            : new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["access_token"] = accessToken
            });

    [Theory]
    [InlineData("/hubs/notifications")]
    [InlineData("/api/hubs/notifications")]
    [InlineData("/api/hubs/notifications/negotiate")]
    public void A_hub_request_is_authenticated_from_the_query_string(string path)
    {
        Assert.Equal("abc.def.ghi", HubAccessToken.FromQuery(new PathString(path), Query("abc.def.ghi")));
    }

    [Theory]
    [InlineData("/api/DesktopIntegration/sales")]
    [InlineData("/api/health")]
    [InlineData("/swagger")]
    [InlineData("/")]
    public void Everything_else_keeps_using_the_header(string path)
    {
        // Returning null leaves context.Token alone, so the Authorization header still speaks.
        Assert.Null(HubAccessToken.FromQuery(new PathString(path), Query("abc.def.ghi")));
    }

    [Fact]
    public void A_hub_request_without_a_token_in_the_query_is_left_alone()
    {
        Assert.Null(HubAccessToken.FromQuery(new PathString("/api/hubs/notifications"), Query(null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_token_is_not_a_token(string token)
    {
        Assert.Null(HubAccessToken.FromQuery(new PathString("/api/hubs/notifications"), Query(token)));
    }

    [Theory]
    [InlineData("/hubs", true)]
    [InlineData("/api/hubs", true)]
    [InlineData("/hubsomething", false)]
    [InlineData("/api/hubsomething", false)]
    public void The_hub_paths_are_matched_by_segment_not_by_prefix(string path, bool isHub)
    {
        // "/hubsomething" starts with the same letters. StartsWithSegments is what keeps a route
        // called /hubspot from being handed tokens out of query strings.
        Assert.Equal(isHub, HubAccessToken.IsHubPath(new PathString(path)));
    }
}
