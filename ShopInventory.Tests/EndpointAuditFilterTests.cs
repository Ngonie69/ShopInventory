using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Features.DesktopIntegration;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Features.VanSalesReports;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what the three blanket audit filters record.
///
/// The desktop surface had no audit at all: 70 endpoints, and of its handlers only the transfer
/// request and the by-hand SAP post wrote a row. A till could sell, queue an invoice, cancel a queued
/// one, retry it or consolidate the day's takings into SAP and leave nothing behind but the documents
/// themselves. The filter closes that by auditing the controller rather than a list of endpoints, so
/// an action added later is covered without anyone remembering to add it.
///
/// Reads are where the surfaces differ, and the difference is deliberate — see each filter. The
/// handset audits everything, the desktop only its writes, and the van sales portal only its reads,
/// because its writes already log from their handlers. The tests below pin each of those.
/// </summary>
public sealed class EndpointAuditFilterTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Desktop_filter_records_every_write(string method)
    {
        var audit = new RecordingAuditService();

        await RunAsync(DesktopFilter(audit), method, "/api/DesktopIntegration/sales", "CreateDesktopSale");

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("DesktopCreateDesktopSale", entry.Action);
        Assert.Equal("/api/DesktopIntegration/sales", entry.EntityId);
        Assert.True(entry.Success);
        Assert.Equal($"{method} /api/DesktopIntegration/sales returned 200.", entry.Details);
    }

    [Fact]
    public async Task Desktop_filter_leaves_polling_reads_out()
    {
        var audit = new RecordingAuditService();

        await RunAsync(DesktopFilter(audit), "GET", "/api/DesktopIntegration/stock/BYO01", "GetAvailableStockBulk");

        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// A refusal is the row most worth having: it says somebody tried to move money and could not.
    /// The status and the problem's detail both have to survive, or the trail records an attempt
    /// without recording that it failed.
    /// </summary>
    [Fact]
    public async Task Desktop_filter_records_a_refused_write_as_a_failure()
    {
        var audit = new RecordingAuditService();
        var refusal = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Stock is locked",
            Detail = "Stock is currently being modified by another sale."
        })
        {
            StatusCode = StatusCodes.Status409Conflict
        };

        await RunAsync(
            DesktopFilter(audit), "POST", "/api/DesktopIntegration/sales", "CreateDesktopSale", result: refusal);

        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
        Assert.Equal("Stock is currently being modified by another sale.", entry.Error);
        Assert.Equal("POST /api/DesktopIntegration/sales returned 409.", entry.Details);
    }

    /// <summary>
    /// A handler that throws still has to leave a row. The exception propagates — the filter is not
    /// an error handler — but the attempt is recorded on the way past.
    /// </summary>
    [Fact]
    public async Task Desktop_filter_records_a_thrown_write_and_rethrows()
    {
        var audit = new RecordingAuditService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(
            DesktopFilter(audit),
            "POST",
            "/api/DesktopIntegration/end-of-day/consolidate",
            "ConsolidateDailySales",
            throws: new InvalidOperationException("SAP is not answering")));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("DesktopConsolidateDailySales", entry.Action);
        Assert.False(entry.Success);
        Assert.Equal("SAP is not answering", entry.Error);
    }

    /// <summary>
    /// An audit failure must never cost the caller their sale. The row is best-effort; the response
    /// is not.
    /// </summary>
    [Fact]
    public async Task Desktop_filter_swallows_its_own_failure()
    {
        var filter = new DesktopIntegrationAuditFilter(
            new ThrowingScopeFactory(), NullLogger<DesktopIntegrationAuditFilter>.Instance);

        await RunAsync(filter, "POST", "/api/DesktopIntegration/sales", "CreateDesktopSale");
    }

    /// <summary>
    /// The handset surface keeps its reads. Pinned because the shared base now has a switch for
    /// dropping them, and van sales must not quietly acquire it.
    /// </summary>
    [Fact]
    public async Task Van_sales_filter_still_records_reads()
    {
        var audit = new RecordingAuditService();

        await RunAsync(VanFilter(audit), "GET", "/api/vansales/customer", "GetCustomers");

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("VanSalesGetCustomers", entry.Action);
        Assert.Equal("/api/vansales/customer", entry.EntityId);
    }

    /// <summary>
    /// A report row without its parameters says only that somebody opened a report. The query string
    /// is what says whose figures, on which route, for which period.
    /// </summary>
    [Fact]
    public async Task Portal_filter_records_a_report_read_with_its_query()
    {
        var audit = new RecordingAuditService();

        await RunAsync(
            PortalFilter(audit),
            "GET",
            "/api/van-sales/performance-report",
            "GetPerformanceReport",
            queryString: "?userId=7b1c&fromDate=2026-08-01&toDate=2026-08-31");

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("VanSalesPortalGetPerformanceReport", entry.Action);
        Assert.Equal("/api/van-sales/performance-report", entry.EntityId);
        Assert.Equal(
            "GET /api/van-sales/performance-report?userId=7b1c&fromDate=2026-08-01&toDate=2026-08-31 returned 200.",
            entry.Details);
    }

    /// <summary>Every portal write already logs from its handler. A second row would be noise.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Portal_filter_leaves_writes_to_their_handlers(string method)
    {
        var audit = new RecordingAuditService();

        await RunAsync(PortalFilter(audit), method, "/api/van-sales/route-stops", "CreateRouteStop");

        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// Recording the query string is opt-in, and pinned off for the two surfaces that did not opt in:
    /// a query string carries whatever a caller put there, and a handset or a till has no reason to be
    /// trusted not to put a token in one.
    /// </summary>
    [Fact]
    public async Task Filters_that_did_not_opt_in_never_record_the_query_string()
    {
        var audit = new RecordingAuditService();

        await RunAsync(VanFilter(audit), "GET", "/api/vansales/customer", "GetCustomers", queryString: "?token=secret");
        await RunAsync(DesktopFilter(audit), "POST", "/api/DesktopIntegration/sales", "CreateDesktopSale", queryString: "?token=secret");

        Assert.Equal(2, audit.Entries.Count);
        Assert.All(audit.Entries, entry => Assert.DoesNotContain("secret", entry.Details));
    }

    /// <summary>
    /// The filter is only worth anything if it is attached. A blanket filter is easy to detach by
    /// accident — it is one attribute on a class that nobody editing an endpoint has reason to read —
    /// and detaching it silences 70 endpoints at once, with no failing behaviour to notice.
    /// </summary>
    [Theory]
    [InlineData(typeof(ShopInventory.Controllers.DesktopIntegrationController), typeof(DesktopIntegrationAuditFilter))]
    [InlineData(typeof(ShopInventory.Controllers.VanSalesCompatibilityController), typeof(VanSalesAuditFilter))]
    [InlineData(typeof(ShopInventory.Controllers.VanSalesReportController), typeof(VanSalesPortalReadAuditFilter))]
    [InlineData(typeof(ShopInventory.Controllers.VanSalesAttendanceController), typeof(VanSalesPortalReadAuditFilter))]
    public void The_controller_carries_its_audit_filter(Type controller, Type filter)
    {
        var attached = controller
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: true)
            .Cast<ServiceFilterAttribute>()
            .Any(attribute => attribute.ServiceType == filter);

        Assert.True(attached, $"{controller.Name} is missing [ServiceFilter(typeof({filter.Name}))].");
    }

    private static DesktopIntegrationAuditFilter DesktopFilter(IAuditService audit)
        => new(new StubScopeFactory(audit), NullLogger<DesktopIntegrationAuditFilter>.Instance);

    private static VanSalesAuditFilter VanFilter(IAuditService audit)
        => new(new StubScopeFactory(audit), NullLogger<VanSalesAuditFilter>.Instance);

    private static VanSalesPortalReadAuditFilter PortalFilter(IAuditService audit)
        => new(new StubScopeFactory(audit), NullLogger<VanSalesPortalReadAuditFilter>.Instance);

    private static async Task RunAsync(
        IAsyncActionFilter filter,
        string method,
        string path,
        string actionName,
        IActionResult? result = null,
        Exception? throws = null,
        string? queryString = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;

        if (queryString is not null)
        {
            httpContext.Request.QueryString = new QueryString(queryString);
        }

        var descriptor = new ControllerActionDescriptor
        {
            RouteValues = new Dictionary<string, string?> { ["action"] = actionName }
        };

        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var executing = new ActionExecutingContext(
            actionContext, [], new Dictionary<string, object?>(), controller: new object());

        await filter.OnActionExecutionAsync(executing, () =>
        {
            if (throws is not null)
            {
                throw throws;
            }

            return Task.FromResult(new ActionExecutedContext(actionContext, [], controller: new object())
            {
                Result = result ?? new OkObjectResult(new { ok = true })
            });
        });
    }

    /// <summary>Hands the filter the recording audit service through the scope it asks for.</summary>
    private sealed class StubScopeFactory(IAuditService audit)
        : IServiceScopeFactory, IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IAuditService) ? audit : null;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("no scope for you");
    }
}
