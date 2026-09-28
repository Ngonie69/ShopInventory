using ShopInventory.Web.Common;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The dashboard figures are read once and shared, rather than read by every open dashboard.
/// </summary>
/// <remarks>
/// Today's invoice value walks up to ten pages of SAP invoices one after another. Each dashboard view
/// used to do that for itself, so twenty open dashboards were twenty walks.
/// </remarks>
public sealed class SharedDashboardFiguresTests : IDisposable
{
    private readonly ManualClock _clock = new();

    public SharedDashboardFiguresTests()
    {
        SharedFigures.ResetForTests();
        SharedFigures.Clock = _clock;
    }

    public void Dispose()
    {
        SharedFigures.ResetForTests();
        SharedFigures.Clock = TimeProvider.System;
    }

    [Fact]
    public async Task Twenty_dashboards_opening_at_once_walk_the_invoices_once()
    {
        var invoices = new FakeInvoices(totalCount: 150) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var date = DateTime.Today.AddDays(-400);

        var views = Enumerable.Range(0, 20)
            .Select(_ => DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, date, includeValue: true))
            .ToList();
        invoices.Gate.SetResult();
        var figures = await Task.WhenAll(views);

        // One walk: two pages for 150 invoices.
        Assert.Equal(2, invoices.Calls);
        Assert.All(figures, figure => Assert.Equal((150, 150m), figure));
    }

    [Fact]
    public async Task A_read_that_failed_is_not_kept()
    {
        var invoices = new FakeInvoices(totalCount: 3) { Fails = true };
        var date = DateTime.Today.AddDays(-401);

        Assert.Equal((0, 0m), await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, date, includeValue: true));

        invoices.Fails = false;
        Assert.Equal((3, 3m), await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, date, includeValue: true));
    }

    [Fact]
    public async Task A_figure_is_read_again_once_its_lifetime_has_passed()
    {
        var invoices = new FakeInvoices(totalCount: 5);
        var date = DateTime.Today.AddDays(-402);

        await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, date, includeValue: false);
        await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, date, includeValue: false);
        Assert.Equal(1, invoices.Calls);

        _clock.Advance(SharedFigures.EarlierDay + TimeSpan.FromSeconds(1));
        await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, date, includeValue: false);
        Assert.Equal(2, invoices.Calls);
    }

    [Fact]
    public async Task Different_days_and_different_questions_are_not_shared()
    {
        var invoices = new FakeInvoices(totalCount: 5);
        var day = DateTime.Today.AddDays(-403);

        await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, day, includeValue: false);
        await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, day.AddDays(-1), includeValue: false);
        await DashboardFigures.GetInvoiceDayTotalsAsync(invoices.Service, day, includeValue: true);

        Assert.Equal(3, invoices.Calls);
    }

    [Fact]
    public void Today_is_shared_for_a_minute_and_earlier_days_for_ten()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SharedFigures.LifetimeFor(DateTime.Today));
        Assert.Equal(TimeSpan.FromMinutes(10), SharedFigures.LifetimeFor(DateTime.Today.AddDays(-1)));
    }

    /// <summary>A day of <c>totalCount</c> invoices worth 1.00 each, paged a hundred at a time.</summary>
    private sealed class FakeInvoices
    {
        private int _calls;

        public FakeInvoices(int totalCount)
        {
            Service = StubProxy.For<IInvoiceService>((method, args) => method.Name switch
            {
                nameof(IInvoiceService.GetInvoicesByDateRangeAsync) => Read((int?)args![2] ?? 1, (int?)args[3] ?? 20),
                _ => throw new InvalidOperationException($"IInvoiceService.{method.Name} was not expected.")
            });
            TotalCount = totalCount;
        }

        public IInvoiceService Service { get; }
        public int TotalCount { get; }
        public bool Fails { get; set; }
        public TaskCompletionSource? Gate { get; init; }
        public int Calls => Volatile.Read(ref _calls);

        private async Task<InvoiceDateResponse?> Read(int page, int pageSize)
        {
            Interlocked.Increment(ref _calls);
            if (Gate is not null)
                await Gate.Task;
            if (Fails)
                return null;

            var onPage = Math.Max(0, Math.Min(pageSize, TotalCount - (page - 1) * pageSize));
            return new InvoiceDateResponse
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = TotalCount,
                Invoices = Enumerable.Range(0, onPage).Select(_ => new InvoiceDto { DocTotal = 1m }).ToList()
            };
        }
    }
}
