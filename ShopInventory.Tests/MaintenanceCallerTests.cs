using Microsoft.AspNetCore.Http;
using ShopInventory.Features.Maintenance;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Which audience a request belongs to, and the header the Web has to send for that to work.
///
/// <para>
/// The lockout decides what to refuse by audience, so a request classified wrongly is either
/// refused when it should not be or — the direction that matters — waved through a freeze somebody
/// believes is total. The classification has no "don't know" answer for that reason, and the tests
/// below pin both halves: what each kind of caller is recognised as, and that the two projects
/// spell the portal's header the same way.
/// </para>
/// </summary>
public sealed class MaintenanceCallerTests
{
    private static MaintenanceCaller Classify(params (string Name, string Value)[] headers)
    {
        var dictionary = new HeaderDictionary();
        foreach (var (name, value) in headers)
        {
            dictionary[name] = value;
        }

        return MaintenanceCaller.FromHeaders(dictionary);
    }

    [Fact]
    public void A_handset_is_the_mobile_audience_and_names_its_app()
    {
        var caller = Classify(
            ("X-App-Id", "com.kefalos.vansales"),
            ("X-App-Platform", "android"),
            ("X-App-Version", "2.0.1"));

        Assert.Equal(MaintenanceAudience.MobileApps, caller.Audience);
        Assert.Equal("kefalos-vansales", caller.PolicyKey);
    }

    [Fact]
    public void A_handset_on_a_build_too_old_to_name_itself_is_still_a_handset()
    {
        // Unidentified is not the same as not a phone. A blanket lockout has to catch this caller,
        // which is what MaintenanceState.CoversApp then decides.
        var caller = Classify(("X-Device-Model", "Pixel 7a"));

        Assert.Equal(MaintenanceAudience.MobileApps, caller.Audience);
        Assert.Null(caller.PolicyKey);
    }

    [Fact]
    public void The_web_portal_is_recognised_by_its_client_header()
    {
        var caller = Classify(("X-Client-App", "web-portal"));

        Assert.Equal(MaintenanceAudience.WebPortal, caller.Audience);
    }

    [Theory]
    [InlineData("WEB-PORTAL")]
    [InlineData("  web-portal  ")]
    public void The_portals_header_is_read_loosely(string value)
    {
        Assert.Equal(MaintenanceAudience.WebPortal, Classify(("X-Client-App", value)).Audience);
    }

    [Fact]
    public void A_caller_claiming_to_be_the_portal_while_sending_android_headers_is_a_phone()
    {
        // Phones are decided first, deliberately. Otherwise the stricter of the two gates could be
        // escaped by adding one header, and the mobile lockout is the stricter one.
        var caller = Classify(
            ("X-Client-App", "web-portal"),
            ("X-App-Platform", "android"),
            ("X-App-Id", "com.kefalos.vansales"));

        Assert.Equal(MaintenanceAudience.MobileApps, caller.Audience);
    }

    [Fact]
    public void Everything_else_falls_into_the_catch_all()
    {
        // The till, the transfer listener, a script, a bare curl. There is no fourth answer, and
        // there must not be: a request that fell through the bottom would be one no lockout could
        // ever stop.
        Assert.Equal(MaintenanceAudience.OtherClients, Classify().Audience);
        Assert.Equal(MaintenanceAudience.OtherClients, Classify(("X-Client-App", "something-else")).Audience);
        Assert.Equal(MaintenanceAudience.OtherClients, Classify(("X-App-Platform", "ios")).Audience);
    }

    [Fact]
    public void The_api_and_the_web_spell_the_portals_header_the_same_way()
    {
        // The Web does not reference the API project, so both sides carry the literal. Drift here
        // would be silent and one-directional: the lockout would stop covering the portal while the
        // settings screen still showed the tick.
        Assert.Equal(MaintenanceCaller.ClientAppHeaderName, WebApiClientIdentity.ClientAppHeaderName);
        Assert.Equal(MaintenanceCaller.WebPortalClientApp, WebApiClientIdentity.ClientAppValue);
    }

    [Fact]
    public void What_the_web_puts_on_a_client_is_what_the_api_reads_as_the_portal()
    {
        // The end-to-end version of the test above: build the header the way the Web does, then
        // classify it the way the API does.
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        WebApiClientIdentity.IdentifyAsWebPortal(client);

        var headers = new HeaderDictionary();
        foreach (var header in client.DefaultRequestHeaders)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        Assert.Equal(MaintenanceAudience.WebPortal, MaintenanceCaller.FromHeaders(headers).Audience);
    }
}
