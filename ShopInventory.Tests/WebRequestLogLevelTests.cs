using Microsoft.AspNetCore.Http;
using Serilog.Events;
using ShopInventory.Web.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Pins which Web requests reach the request log at Information.
/// </summary>
public class WebRequestLogLevelTests
{
    [Theory]
    [InlineData("/css/van-sales.css")]
    [InlineData("/app.b1c2d3.css")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.js")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/js/app.js")]
    [InlineData("/favicon.ico")]
    public void A_served_asset_drops_below_the_log_minimum(string path)
    {
        Assert.Equal(LogEventLevel.Verbose, RequestLogLevels.For(Request(path, 200), 3, null));
        Assert.Equal(LogEventLevel.Verbose, RequestLogLevels.For(Request(path, 304), 1, null));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/desktop-sales")]
    [InlineData("/api/download/invoice/12")]
    [InlineData("/_blazor")]
    public void A_page_or_endpoint_still_logs_at_information(string path)
    {
        Assert.Equal(LogEventLevel.Information, RequestLogLevels.For(Request(path, 200), 40, null));
    }

    [Fact]
    public void A_missing_asset_still_logs_at_information()
    {
        Assert.Equal(LogEventLevel.Information, RequestLogLevels.For(Request("/css/gone.css", 404), 2, null));
    }

    [Fact]
    public void A_failing_asset_still_logs_as_an_error()
    {
        Assert.Equal(LogEventLevel.Error, RequestLogLevels.For(Request("/css/app.css", 500), 5, null));
        Assert.Equal(
            LogEventLevel.Error,
            RequestLogLevels.For(Request("/css/app.css", 200), 5, new InvalidOperationException("broken")));
    }

    private static HttpContext Request(string path, int status)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.StatusCode = status;
        return context;
    }
}
