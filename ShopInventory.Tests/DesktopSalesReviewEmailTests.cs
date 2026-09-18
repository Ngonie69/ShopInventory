using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;
using ShopInventory.Features.DesktopIntegration.Commands.UpdateDesktopSalesReviewSchedule;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the review email: which period each cadence covers, that the schedule saved from Settings is the
/// one the job reads, and that a scheduled period goes out once — never twice, and never weeks late.
/// </summary>
public sealed class DesktopSalesReviewEmailTests : IDisposable
{
    // Monday 21 Sep 2026, 07:00 CAT: the first run after the week of 14–20 Sep.
    private static readonly DateTimeOffset MondayMorning = new(2026, 9, 21, 5, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingEmail _email = new();
    private readonly ManualClock _clock = new();
    private readonly Guid _adminId = Guid.NewGuid();
    private int _reference;

    public DesktopSalesReviewEmailTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Shops.Add(new ShopEntity { Code = "FACTORY", Name = "Kefalos Factory Shop & Co", BusinessPartnerCode = "KEFSHOP-BP", WarehouseCode = "KEFSHOP", IsActive = true });
        _context.Users.Add(new User { Id = _adminId, Username = "admin", FirstName = "Tariro", LastName = "Moyo", PasswordHash = "x", Role = ApplicationRoles.Admin, IsActive = true });
        _context.SaveChanges();

        foreach (var day in Enumerable.Range(14, 7))
        {
            AddSale(new DateTime(2026, 9, day), 25m);
        }

        _clock.Advance(MondayMorning - _clock.GetUtcNow());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- Periods ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-09-21", "2026-09-14", "2026-09-20")] // Monday: the week just ended
    [InlineData("2026-09-23", "2026-09-14", "2026-09-20")] // Wednesday: still that week
    [InlineData("2026-09-20", "2026-09-07", "2026-09-13")] // Sunday: this week has not ended
    public void A_weekly_review_covers_the_last_complete_Monday_to_Sunday(string today, string from, string to)
    {
        var period = DesktopSalesReviewCadence.LastComplete(DesktopSalesReviewCadence.Weekly, DateTime.Parse(today));

        Assert.Equal((DateTime.Parse(from), DateTime.Parse(to)), period);
    }

    [Fact]
    public void A_monthly_review_covers_the_calendar_month_just_ended()
    {
        var period = DesktopSalesReviewCadence.LastComplete(DesktopSalesReviewCadence.Monthly, new DateTime(2026, 10, 1));

        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), period);
    }

    // ---- The schedule -----------------------------------------------------------------------------

    [Fact]
    public async Task The_schedule_saved_is_the_schedule_read_and_the_saver_becomes_the_reader()
    {
        var saved = await SaveScheduleAsync(weekly: true, monthly: false, " owner@kef.co.zw ; OWNER@kef.co.zw, accounts@kef.co.zw");

        Assert.True(saved.WeeklyEnabled);
        Assert.False(saved.MonthlyEnabled);
        Assert.Equal(["owner@kef.co.zw", "accounts@kef.co.zw"], saved.Recipients);
        Assert.Equal(_adminId, saved.SendAsUserId);
        Assert.Equal("Tariro Moyo", saved.SendAsUserName);
    }

    [Fact]
    public void Switching_the_review_on_with_nobody_to_send_it_to_is_refused()
    {
        var result = new UpdateDesktopSalesReviewScheduleValidator()
            .Validate(new UpdateDesktopSalesReviewScheduleCommand(_adminId, "admin", true, false, []));

        Assert.False(result.IsValid);
    }

    // ---- Scheduled sends --------------------------------------------------------------------------

    [Fact]
    public async Task A_switched_off_review_sends_nothing()
    {
        await SaveScheduleAsync(weekly: false, monthly: false, "owner@kef.co.zw");

        var result = await SendAsync(new SendDesktopSalesReviewEmailCommand(DesktopSalesReviewCadence.Weekly, Scheduled: true));

        Assert.True(result.Value.Skipped);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task A_scheduled_week_goes_to_every_recipient_with_the_review_attached_and_only_once()
    {
        await SaveScheduleAsync(weekly: true, monthly: false, "owner@kef.co.zw, accounts@kef.co.zw");
        AddSale(new DateTime(2026, 9, 18), 5m, "KEFGRS");

        var first = await SendAsync(new SendDesktopSalesReviewEmailCommand(DesktopSalesReviewCadence.Weekly, Scheduled: true));

        Assert.False(first.IsError, first.IsError ? first.FirstError.Description : null);
        Assert.Equal(new DateTime(2026, 9, 14), first.Value.FromDate);
        Assert.Equal(new DateTime(2026, 9, 20), first.Value.ToDate);
        Assert.Equal(["owner@kef.co.zw", "accounts@kef.co.zw"], _email.Sent.Select(m => m.To));

        var message = _email.Sent[0];
        Assert.Equal("Weekly sales review: 14 Sep to 20 Sep 2026", message.Subject);
        Assert.Contains("USD 180.00", message.Body);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("application/pdf", attachment.mimeType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(attachment.content, 0, 4));

        // The shop's own name is data, and data is encoded.
        Assert.Contains("Kefalos Factory Shop &amp; Co", message.Body);

        // Tuesday's run finds the week already sent.
        _clock.Advance(TimeSpan.FromDays(1));
        var again = await SendAsync(new SendDesktopSalesReviewEmailCommand(DesktopSalesReviewCadence.Weekly, Scheduled: true));

        Assert.True(again.Value.Skipped);
        Assert.Equal(2, _email.Sent.Count);
    }

    [Fact]
    public async Task A_missed_Monday_is_caught_up_but_not_a_week_later()
    {
        await SaveScheduleAsync(weekly: true, monthly: false, "owner@kef.co.zw");

        _clock.Advance(TimeSpan.FromDays(2));   // Wednesday: within the catch-up window
        var caughtUp = await SendAsync(new SendDesktopSalesReviewEmailCommand(DesktopSalesReviewCadence.Weekly, Scheduled: true));
        Assert.False(caughtUp.Value.Skipped);

        await ForgetLastSentAsync();
        _clock.Advance(TimeSpan.FromDays(2));   // Friday: too late for that week
        var late = await SendAsync(new SendDesktopSalesReviewEmailCommand(DesktopSalesReviewCadence.Weekly, Scheduled: true));
        Assert.True(late.Value.Skipped);
    }

    [Fact]
    public async Task A_send_nobody_received_is_an_error_and_is_retried_next_run()
    {
        await SaveScheduleAsync(weekly: true, monthly: false, "owner@kef.co.zw");
        _email.Accepts = false;

        var refused = await SendAsync(new SendDesktopSalesReviewEmailCommand(DesktopSalesReviewCadence.Weekly, Scheduled: true));
        Assert.True(refused.IsError);

        _email.Accepts = true;
        var retried = await SendAsync(new SendDesktopSalesReviewEmailCommand(DesktopSalesReviewCadence.Weekly, Scheduled: true));
        Assert.False(retried.Value.Skipped);
    }

    // ---- Sends by hand ----------------------------------------------------------------------------

    [Fact]
    public async Task A_review_sent_by_hand_goes_now_to_whoever_is_named_and_does_not_mark_the_schedule()
    {
        await SaveScheduleAsync(weekly: true, monthly: false, "owner@kef.co.zw");

        var result = await SendAsync(new SendDesktopSalesReviewEmailCommand(
            DesktopSalesReviewCadence.Custom, Scheduled: false, _adminId, "admin",
            new DateTime(2026, 9, 14), new DateTime(2026, 9, 16), ["me@kef.co.zw"]));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(["me@kef.co.zw"], _email.Sent.Select(m => m.To));
        Assert.Equal("Sales review: 14 Sep to 16 Sep 2026", _email.Sent[0].Subject);
        Assert.Null((await ScheduleAsync()).LastWeeklyPeriodEnd);
    }

    // ---- Plumbing ---------------------------------------------------------------------------------

    private void AddSale(DateTime day, decimal total, string warehouse = "KEFSHOP")
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = $"REF-{++_reference}",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = $"{warehouse}-BP",
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
            Lines = [new DesktopSaleLineEntity { LineNum = 1, ItemCode = "TUB5L", ItemDescription = "5 Litre Vanilla", Quantity = 1, UnitPrice = 21.65m, LineTotal = 21.65m, WarehouseCode = "KEFSHOP" }],
        });
        _context.SaveChanges();
    }

    private async Task<DesktopSalesReviewSchedule> SaveScheduleAsync(bool weekly, bool monthly, string recipients)
    {
        var result = await new UpdateDesktopSalesReviewScheduleHandler(
                _context, Mediator(), new RecordingAuditService(), NullLogger<UpdateDesktopSalesReviewScheduleHandler>.Instance)
            .Handle(new UpdateDesktopSalesReviewScheduleCommand(_adminId, "admin", weekly, monthly, [recipients]), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<DesktopSalesReviewSchedule> ScheduleAsync() =>
        (await new GetDesktopSalesReviewScheduleHandler(_context).Handle(new GetDesktopSalesReviewScheduleQuery(), CancellationToken.None)).Value;

    private async Task ForgetLastSentAsync()
    {
        await _context.SystemConfigs.Where(c => c.Key.StartsWith("DesktopSalesReview.LastSent.")).ExecuteDeleteAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<ErrorOr<DesktopSalesReviewEmailResult>> SendAsync(SendDesktopSalesReviewEmailCommand command)
    {
        _context.ChangeTracker.Clear();
        return await new SendDesktopSalesReviewEmailHandler(
                _context, Mediator(), _email, new RecordingAuditService(), NullLogger<SendDesktopSalesReviewEmailHandler>.Instance, _clock)
            .Handle(command, CancellationToken.None);
    }

    /// <summary>Every query the send reads, run for real against the test database.</summary>
    private IMediator Mediator()
    {
        IMediator mediator = null!;
        mediator = StubProxy.For<IMediator>((method, args) => method.Name != nameof(IMediator.Send) ? null : args?[0] switch
        {
            GetDesktopSalesReviewScheduleQuery schedule =>
                new GetDesktopSalesReviewScheduleHandler(_context).Handle(schedule, CancellationToken.None),
            GetDesktopSalesReviewPdfQuery pdf =>
                new GetDesktopSalesReviewPdfHandler(mediator).Handle(pdf, CancellationToken.None),
            GetDesktopSalesReviewQuery review =>
                new GetDesktopSalesReviewHandler(_context, mediator).Handle(review, CancellationToken.None),
            GetDesktopSalesAnalysisQuery analysis =>
                new GetDesktopSalesAnalysisHandler(_context, new RecordingAuditService()).Handle(analysis, CancellationToken.None),
            GetManagementSalesReportQuery management =>
                new GetManagementSalesReportHandler(_context, new RecordingAuditService(), new NoCost(), NullLogger<GetManagementSalesReportHandler>.Instance)
                    .Handle(management, CancellationToken.None),
            _ => null
        });
        return mediator;
    }

    private sealed class NoCost : ISaleInvoiceCostReader
    {
        public Task<IReadOnlyList<SaleInvoiceLineCost>> ReadAsync(
            IReadOnlyCollection<string> warehouseCodes, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken) =>
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
