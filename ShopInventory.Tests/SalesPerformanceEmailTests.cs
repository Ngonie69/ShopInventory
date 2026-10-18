using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;
using ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;
using ShopInventory.Features.ManagementSales.Commands.UpdateSalesPerformanceEmailSchedule;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;
using ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Xunit.Abstractions;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the management sales email: that shops, vending and vans are each read from their own report and
/// set side by side over the same days, that the van findings are written from the figures, and that a
/// scheduled period goes out once.
/// </summary>
public sealed class SalesPerformanceEmailTests : IDisposable
{
    // Thursday 15 Oct 2026, 07:30 CAT: the daily run for Wednesday the 14th, compared with Tuesday the 13th.
    private static readonly DateTimeOffset ThursdayMorning = new(2026, 10, 15, 5, 30, 0, TimeSpan.Zero);
    private static readonly DateTime Tuesday = new(2026, 10, 13);
    private static readonly DateTime Wednesday = new(2026, 10, 14);

    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherRep = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly ITestOutputHelper _output;
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingEmail _email = new();
    private readonly ManualClock _clock = new();
    private readonly Guid _adminId = Guid.NewGuid();
    private int _reference;
    private bool _sapDown;

    public SalesPerformanceEmailTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Shops.Add(new ShopEntity { Code = "FACTORY", Name = "Kefalos Factory Shop & Co", BusinessPartnerCode = "KEFSHOP-BP", WarehouseCode = "KEFSHOP", IsActive = true });
        _context.Users.Add(new User { Id = _adminId, Username = "admin", FirstName = "Tariro", LastName = "Moyo", PasswordHash = "x", Role = ApplicationRoles.Admin, IsActive = true });
        AddRep(Rep, "van010", "Tinashe", "Moyo", "VAN010");
        AddRep(OtherRep, "van011", "Rudo", "Chikanga", "VAN011");
        _context.SaveChanges();

        // Shops: USD 50 on Tuesday, USD 75 on Wednesday.
        AddCounterSale(Tuesday, 25m);
        AddCounterSale(Tuesday, 25m);
        AddCounterSale(Wednesday, 25m);
        AddCounterSale(Wednesday, 25m);
        AddCounterSale(Wednesday, 25m, cardCode: "CUST001", cardName: "Bindura Hotel");

        // Vending: nothing on Tuesday, USD 40 on Wednesday.
        AddCounterSale(Wednesday, 40m, "KEFVEND", SaleSourceSystems.Vending);

        // Vans: Guruve takes 100 then 60; Mazowe takes 30 on Tuesday and nothing on Wednesday.
        AddRouteDay(Rep, Tuesday, "GURUVE");
        AddVanSale(Rep, Tuesday, 100m, "TUCK01");
        AddRouteDay(OtherRep, Tuesday, "MAZOWE");
        AddVanSale(OtherRep, Tuesday, 30m, "SHOP9");
        AddRouteDay(Rep, Wednesday, "GURUVE");
        AddVanSale(Rep, Wednesday, 60m, "TUCK01");
        AddExpiredReservation(Rep, Wednesday);
        _context.SaveChanges();

        _clock.Advance(ThursdayMorning - _clock.GetUtcNow());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- Periods ----------------------------------------------------------------------------------

    [Fact]
    public void A_daily_email_covers_yesterday_and_weeks_and_months_match_the_review()
    {
        var today = new DateTime(2026, 10, 15);

        Assert.Equal((Wednesday, Wednesday), SalesPerformanceEmailCadence.LastComplete(SalesPerformanceEmailCadence.Daily, today));
        Assert.Equal((new DateTime(2026, 10, 5), new DateTime(2026, 10, 11)), SalesPerformanceEmailCadence.LastComplete(SalesPerformanceEmailCadence.Weekly, today));
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), SalesPerformanceEmailCadence.LastComplete(SalesPerformanceEmailCadence.Monthly, today));
    }

    // ---- The figures ------------------------------------------------------------------------------

    [Fact]
    public async Task Shops_vending_and_vans_are_scored_side_by_side_over_the_same_days()
    {
        var performance = await PerformanceAsync();

        Assert.Equal(Wednesday, performance.FromDate);
        Assert.Equal(Tuesday, performance.PreviousFromDate);

        var shops = Score(performance, SaleBusinesses.Shops);
        Assert.Equal(75m, shops.Takings);
        Assert.Equal(50m, shops.PreviousTakings);
        Assert.Equal(50m, shops.ChangePercent);
        Assert.Equal(3, shops.Transactions);

        var vending = Score(performance, SaleBusinesses.Vending);
        Assert.Equal(40m, vending.Takings);
        Assert.Null(vending.ChangePercent);   // nothing the day before: new, not "up infinity"

        var vans = Score(performance, SaleBusinesses.Vans);
        Assert.Equal(60m, vans.Takings);
        Assert.Equal(130m, vans.PreviousTakings);
        Assert.Null(vans.Credited);           // the van report does not read credits; never shown as zero

        var total = Assert.Single(performance.Totals);
        Assert.Equal(175m, total.Takings);
        Assert.Equal(180m, total.PreviousTakings);
        Assert.Equal(-2.8m, total.ChangePercent);
        Assert.Equal(100m, performance.Scorecard.Sum(row => row.SharePercent), 0);
    }

    [Fact]
    public async Task The_van_findings_are_written_from_the_figures()
    {
        var vans = (await PerformanceAsync()).Vans;

        Assert.Equal(1, vans.UnconfirmedSales);

        var guruve = Assert.Single(vans.Routes, route => route.Label == "GURUVE route");
        Assert.Equal(-40m, guruve.ChangePercent);

        Assert.Contains(vans.Findings, f => f.Severity == DesktopSalesReviewSeverity.Action && f.Title == "Van takings down 53.8% in USD");
        Assert.Contains(vans.Findings, f => f.Title == "GURUVE route fell 40%");
        Assert.Contains(vans.Findings, f => f.Title == "MAZOWE route sold nothing this period");
        Assert.Contains(vans.Findings, f => f.Title == "1 van sale never confirmed");

        // Most urgent first.
        Assert.Equal(DesktopSalesReviewSeverity.Action, vans.Findings[0].Severity);
    }

    [Fact]
    public async Task Every_business_partner_is_listed_and_each_channel_adds_up_to_its_scorecard_row()
    {
        var performance = await PerformanceAsync();

        foreach (var row in performance.Scorecard)
        {
            var partners = performance.Partners.Where(p => p.Business == row.Business && p.Currency == row.Currency).ToList();
            Assert.Equal(row.Takings, partners.Sum(p => p.Takings));
            Assert.Equal(row.PreviousTakings, partners.Sum(p => p.PreviousTakings));
            Assert.Equal(row.Transactions, partners.Sum(p => p.Transactions));
        }

        var shops = performance.Partners.Where(p => p.Business == SaleBusinesses.Shops).ToList();
        Assert.Equal(["CIS006", "CUST001"], shops.Select(p => p.CardCode));
        Assert.Equal("Factory Kefalos shop POS USD", shops[0].Name);   // SAP's name, not the till's
        Assert.True(performance.PartnerNamesFromSap);
        Assert.Equal(50m, shops[0].Takings);
        Assert.Equal(25m, shops[1].Takings);

        // A van bills every sale to its own account. Mazowe sold only the day before and is still listed.
        var vans = performance.Partners.Where(p => p.Business == SaleBusinesses.Vans).ToList();
        var guruve = Assert.Single(vans, p => p.CardCode == "VAN010");
        Assert.Equal("VAN 010 GURUVE TRUCK", guruve.Name);
        Assert.Equal("Tinashe Moyo", guruve.Hint);
        Assert.Equal(60m, guruve.Takings);
        Assert.Equal(100m, guruve.PreviousTakings);
        Assert.Equal(100m, guruve.SharePercent);

        var mazowe = Assert.Single(vans, p => p.CardCode == "VAN011");
        Assert.Equal("Van 011 Mazowe", mazowe.Name);   // unknown to SAP: keeps the name on its sales
        Assert.Equal(0m, mazowe.Takings);
        Assert.Equal(30m, mazowe.PreviousTakings);
        Assert.Null(mazowe.Credited);
    }

    [Fact]
    public async Task Without_SAP_each_partner_keeps_the_name_on_its_sales_and_the_email_says_so()
    {
        _sapDown = true;

        var performance = await PerformanceAsync();

        Assert.False(performance.PartnerNamesFromSap);
        Assert.Equal("KEFSHOP till", Assert.Single(performance.Partners, p => p.CardCode == "CIS006").Name);
        Assert.Equal(175m, performance.Totals.Single().Takings);
    }

    // ---- The schedule -----------------------------------------------------------------------------

    [Fact]
    public async Task The_schedule_saved_is_the_schedule_read_and_the_saver_becomes_the_reader()
    {
        var saved = await SaveScheduleAsync(daily: true, weekly: false, monthly: true, " md@kef.co.zw ; MD@kef.co.zw, finance@kef.co.zw");

        Assert.True(saved.DailyEnabled);
        Assert.False(saved.WeeklyEnabled);
        Assert.True(saved.MonthlyEnabled);
        Assert.Equal(["md@kef.co.zw", "finance@kef.co.zw"], saved.Recipients);
        Assert.Equal(_adminId, saved.SendAsUserId);
        Assert.Equal("Tariro Moyo", saved.SendAsUserName);
    }

    [Fact]
    public void Switching_the_email_on_with_nobody_to_send_it_to_is_refused()
    {
        var result = new UpdateSalesPerformanceEmailScheduleValidator()
            .Validate(new UpdateSalesPerformanceEmailScheduleCommand(_adminId, "admin", true, false, false, []));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task A_switched_off_cadence_sends_nothing()
    {
        await SaveScheduleAsync(daily: false, weekly: true, monthly: false, "md@kef.co.zw");

        var result = await SendAsync(new SendSalesPerformanceEmailCommand(SalesPerformanceEmailCadence.Daily, Scheduled: true));

        Assert.True(result.Value.Skipped);
        Assert.Empty(_email.Sent);
    }

    // ---- Sends ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_scheduled_day_goes_to_every_recipient_with_all_three_businesses_and_only_once()
    {
        await SaveScheduleAsync(daily: true, weekly: false, monthly: false, "md@kef.co.zw, finance@kef.co.zw");

        var first = await SendAsync(new SendSalesPerformanceEmailCommand(SalesPerformanceEmailCadence.Daily, Scheduled: true));

        Assert.False(first.IsError, first.IsError ? first.FirstError.Description : null);
        Assert.Equal(Wednesday, first.Value.FromDate);
        Assert.Equal(Wednesday, first.Value.ToDate);
        Assert.Equal(["md@kef.co.zw", "finance@kef.co.zw"], _email.Sent.Select(m => m.To));

        var message = _email.Sent[0];
        _output.WriteLine(message.Body);

        Assert.Equal("Daily sales performance: Wed 14 Oct 2026", message.Subject);
        Assert.Contains("USD 175.00", message.Body);
        Assert.Contains(">Shops<", message.Body);
        Assert.Contains(">Vending<", message.Body);
        Assert.Contains(">Van sales<", message.Body);
        Assert.Contains("GURUVE route", message.Body);
        Assert.Contains("Tinashe Moyo", message.Body);
        Assert.Contains("Needs attention", message.Body);
        Assert.Contains("Bindura Hotel", message.Body);
        Assert.Contains("Factory Kefalos shop POS USD", message.Body);
        Assert.Contains("CIS006", message.Body);
        Assert.DoesNotContain("CUST001 · CUST001", message.Body);
        Assert.Contains("Van 011 Mazowe", message.Body);
        Assert.Contains("Shops combined", message.Body);
        Assert.Contains("Van sales combined", message.Body);
        Assert.Contains("Combined per channel", message.Body);

        // Shop names are data, and data is encoded.
        Assert.Contains("Kefalos Factory Shop &amp; Co", message.Body);

        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("application/pdf", attachment.mimeType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(attachment.content, 0, 4));

        // A rerun the same morning finds the day already sent.
        var again = await SendAsync(new SendSalesPerformanceEmailCommand(SalesPerformanceEmailCadence.Daily, Scheduled: true));
        Assert.True(again.Value.Skipped);
        Assert.Equal(2, _email.Sent.Count);
        Assert.Equal(Wednesday, (await ScheduleAsync()).LastDailyPeriodEnd);
    }

    [Fact]
    public async Task A_send_nobody_received_is_an_error_and_is_retried_next_run()
    {
        await SaveScheduleAsync(daily: true, weekly: false, monthly: false, "md@kef.co.zw");
        _email.Accepts = false;

        var refused = await SendAsync(new SendSalesPerformanceEmailCommand(SalesPerformanceEmailCadence.Daily, Scheduled: true));
        Assert.True(refused.IsError);

        _email.Accepts = true;
        var retried = await SendAsync(new SendSalesPerformanceEmailCommand(SalesPerformanceEmailCadence.Daily, Scheduled: true));
        Assert.False(retried.Value.Skipped);
    }

    [Fact]
    public async Task An_email_sent_by_hand_goes_now_to_whoever_is_named_and_does_not_mark_the_schedule()
    {
        await SaveScheduleAsync(daily: true, weekly: false, monthly: false, "md@kef.co.zw");

        var result = await SendAsync(new SendSalesPerformanceEmailCommand(
            SalesPerformanceEmailCadence.Custom, Scheduled: false, _adminId, "admin", Tuesday, Wednesday, ["me@kef.co.zw"]));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(["me@kef.co.zw"], _email.Sent.Select(m => m.To));
        Assert.Equal("Sales performance: 13 Oct to 14 Oct 2026", _email.Sent[0].Subject);
        Assert.Null((await ScheduleAsync()).LastDailyPeriodEnd);
    }

    // ---- Plumbing ---------------------------------------------------------------------------------

    private static SalesPerformanceScore Score(SalesPerformance performance, string business) =>
        Assert.Single(performance.Scorecard, row => row.Business == business && row.Currency == "USD");

    private async Task<SalesPerformance> PerformanceAsync()
    {
        var result = await new GetSalesPerformanceHandler(_context, Mediator(), Options.Create(new TaxSettings()), Sap(), NullLogger<GetSalesPerformanceHandler>.Instance)
            .Handle(new GetSalesPerformanceQuery(_adminId, Wednesday, Wednesday), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private void AddRep(Guid id, string username, string firstName, string lastName, string account) =>
        _context.Users.Add(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            FirstName = firstName,
            LastName = lastName,
            AssignedWarehouseCode = account,
            AssignedBusinessPartnerCode = account
        });

    private void AddCounterSale(DateTime day, decimal total, string warehouse = "KEFSHOP", string source = SaleSourceSystems.ShopTill, string? cardCode = null, string? cardName = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"REF-{++_reference}",
            SourceSystem = source,
            CardCode = cardCode ?? (warehouse == "KEFSHOP" ? "CIS006" : $"{warehouse}-BP"),
            CardName = cardName ?? $"{warehouse} till",
            WarehouseCode = warehouse,
            DocDate = day,
            TotalAmount = total,
            VatAmount = Math.Round(total * 0.155m / 1.155m, 2),
            AmountPaid = total,
            Currency = "USD",
            PaymentMethod = TenderTypes.Cash,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            CreatedBy = _adminId.ToString(),
            CreatedAt = DateTime.SpecifyKind(day.AddHours(10), DateTimeKind.Utc),
            Lines = [new DesktopSaleLineEntity { LineNum = 1, ItemCode = "TUB5L", ItemDescription = "5 Litre Vanilla", Quantity = 1, UnitPrice = total, LineTotal = total, WarehouseCode = warehouse }],
        });
    }

    private void AddRouteDay(Guid userId, DateTime day, string routeCode) =>
        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = userId,
            Username = userId == Rep ? "van010" : "van011",
            TradingDate = day,
            RouteCode = routeCode,
            RouteName = $"{routeCode} route",
            Territory = "Mash Central",
            DepartedAt = DateTime.SpecifyKind(day.AddHours(5), DateTimeKind.Utc),
            PlannedCustomerCount = 10
        });

    private void AddVanSale(Guid userId, DateTime day, decimal total, string routeCustomerCode) =>
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"VAN-{++_reference}",
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = userId == Rep ? "VAN010" : "VAN011",
            CardName = userId == Rep ? "Van 010 Guruve" : "Van 011 Mazowe",
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = $"Shop {routeCustomerCode}",
            DocDate = day,
            TotalAmount = total,
            VatAmount = 0m,
            Currency = "USD",
            WarehouseCode = userId == Rep ? "VAN010" : "VAN011",
            PaymentMethod = "Cash",
            AmountPaid = total,
            CreatedBy = userId.ToString(),
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheese 1kg",
                    Quantity = 1,
                    UnitPrice = total,
                    LineTotal = total,
                    WarehouseCode = userId == Rep ? "VAN010" : "VAN011"
                }
            ]
        });

    private void AddExpiredReservation(Guid userId, DateTime day)
    {
        var createdAt = DateTime.SpecifyKind(day.AddHours(9), DateTimeKind.Utc);
        _context.StockReservations.Add(new StockReservationEntity
        {
            ReservationId = Guid.NewGuid().ToString(),
            ExternalReferenceId = $"EXP-{++_reference}",
            SourceSystem = SaleSourceSystems.VanSales,
            DocumentType = ReservationDocumentType.Invoice,
            CardCode = "VAN010",
            TotalValue = 20m,
            Currency = "USD",
            Status = ReservationStatus.Expired,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddHours(1),
            CreatedBy = userId.ToString(),
            Lines = []
        });
    }

    private async Task<SalesPerformanceEmailSchedule> SaveScheduleAsync(bool daily, bool weekly, bool monthly, string recipients)
    {
        var result = await new UpdateSalesPerformanceEmailScheduleHandler(
                _context, Mediator(), new RecordingAuditService(), NullLogger<UpdateSalesPerformanceEmailScheduleHandler>.Instance)
            .Handle(new UpdateSalesPerformanceEmailScheduleCommand(_adminId, "admin", daily, weekly, monthly, [recipients]), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<SalesPerformanceEmailSchedule> ScheduleAsync() =>
        (await new GetSalesPerformanceEmailScheduleHandler(_context).Handle(new GetSalesPerformanceEmailScheduleQuery(), CancellationToken.None)).Value;

    private async Task<ErrorOr<SalesPerformanceEmailResult>> SendAsync(SendSalesPerformanceEmailCommand command)
    {
        _context.ChangeTracker.Clear();
        return await new SendSalesPerformanceEmailHandler(
                _context, Mediator(), _email, new RecordingAuditService(), NullLogger<SendSalesPerformanceEmailHandler>.Instance, Options.Create(new TaxSettings()), Sap(), _clock)
            .Handle(command, CancellationToken.None);
    }

    /// <summary>Every query the send reads, run for real against the test database.</summary>
    private IMediator Mediator()
    {
        IMediator mediator = null!;
        mediator = StubProxy.For<IMediator>((method, args) => method.Name != nameof(IMediator.Send) ? null : args?[0] switch
        {
            GetSalesPerformanceEmailScheduleQuery schedule =>
                new GetSalesPerformanceEmailScheduleHandler(_context).Handle(schedule, CancellationToken.None),
            GetDesktopSalesReviewPdfQuery pdf =>
                new GetDesktopSalesReviewPdfHandler(mediator).Handle(pdf, CancellationToken.None),
            GetDesktopSalesReviewQuery review =>
                new GetDesktopSalesReviewHandler(_context, mediator).Handle(review, CancellationToken.None),
            GetDesktopSalesAnalysisQuery analysis =>
                new GetDesktopSalesAnalysisHandler(_context, new RecordingAuditService()).Handle(analysis, CancellationToken.None),
            GetManagementSalesReportQuery management =>
                new GetManagementSalesReportHandler(_context, new RecordingAuditService(), new NoCost(), NullLogger<GetManagementSalesReportHandler>.Instance)
                    .Handle(management, CancellationToken.None),
            GetVanSalesPerformanceReportQuery vans =>
                new GetVanSalesPerformanceReportHandler(_context, Options.Create(new TaxSettings())).Handle(vans, CancellationToken.None),
            _ => null
        });
        return mediator;
    }

    /// <summary>SAP's partner master: the factory shop's POS account and one van, as SAP names them.</summary>
    private ISAPServiceLayerClient Sap() =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetBusinessPartnersByCodesAsync) => _sapDown
                ? Task.FromException<List<DTOs.BusinessPartnerDto>>(new HttpRequestException("SAP is down"))
                : Task.FromResult(new List<DTOs.BusinessPartnerDto>
                {
                    new() { CardCode = "CIS006", CardName = "Factory Kefalos shop POS USD" },
                    new() { CardCode = "VAN010", CardName = "VAN 010 GURUVE TRUCK" },
                }.Where(p => ((IReadOnlyCollection<string>)args![0]!).Contains(p.CardCode!)).ToList()),
            _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private sealed class NoCost : ISaleInvoiceCostReader
    {
        public Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
            IReadOnlyCollection<string> warehouseCodes, IReadOnlyCollection<int> docEntries, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SaleInvoiceLineCost>>([]);
    }

    private sealed class RecordingEmail : IEmailService
    {
        public bool Accepts { get; set; } = true;

        public List<(string To, string Subject, string Body, List<(byte[] content, string fileName, string mimeType)> Attachments)> Sent { get; } = [];

        public Task<bool> SendEmailAsync(
            string toEmail, string subject, string body,
            List<(byte[] content, string fileName, string mimeType)>? attachments = null,
            string[]? ccEmails = null, CancellationToken cancellationToken = default)
        {
            if (Accepts)
            {
                Sent.Add((toEmail, subject, body, attachments ?? []));
            }

            return Task.FromResult(Accepts);
        }

        public Task<DTOs.EmailSentResponseDto> SendEmailAsync(DTOs.SendEmailRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DTOs.EmailSentResponseDto> SendEmailFromTemplateAsync(string templateName, Dictionary<string, string> parameters, List<string> to, string subject, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task QueueEmailAsync(DTOs.SendEmailRequest request, string? category = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ProcessEmailQueueAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DTOs.EmailSentResponseDto> TestEmailConfigurationAsync(string toEmail, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
