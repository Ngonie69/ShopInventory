using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Email;
using ShopInventory.Features.Email.Commands.ProcessEmailQueue;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the email queue: what is queued gets sent, by a job rather than by someone remembering to call
/// <c>POST api/email/process-queue</c>.
/// </summary>
/// <remarks>
/// Password reset emails were queued with a bare address while the only drain read the column as a JSON
/// array, so the one manual drain that existed threw on them and left them stuck in "Sending", and no job
/// ever ran it. Nobody who asked for a reset was ever sent one.
/// </remarks>
public sealed class EmailQueueTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<SendEmailRequest> _sent = [];
    private Func<SendEmailRequest, Task<EmailSentResponseDto>> _send;

    public EmailQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = NewContext();
        _context.Database.EnsureCreated();

        _send = request => Task.FromResult(new EmailSentResponseDto { Success = true, SentAt = Now.UtcDateTime });
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_password_reset_email_is_sent_to_the_user_who_asked()
    {
        // The exact call PasswordResetService makes.
        await using (var context = NewContext())
        {
            await new EmailQueueService(context, NullLogger<EmailQueueService>.Instance)
                .QueueEmailAsync("staff@kefaloscheese.com", "Password Reset Request", "Reset Token: abc", "PasswordReset");
        }

        var result = await Run();

        Assert.Equal(1, result.Sent);
        var sent = Assert.Single(_sent);
        Assert.Equal(["staff@kefaloscheese.com"], sent.To);
        Assert.Equal("Password Reset Request", sent.Subject);
        Assert.False(sent.IsHtml);

        var row = await SingleRowAsync();
        Assert.Equal(EmailQueueStatus.Sent, row.Status);
        Assert.Equal("PasswordReset", row.Category);
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.SentAt);
    }

    [Fact]
    public async Task A_row_queued_with_a_bare_address_before_this_fix_is_still_sent()
    {
        await GivenRowAsync(toAddresses: "a@kefaloscheese.com, b@kefaloscheese.com");

        await Run();

        Assert.Equal(["a@kefaloscheese.com", "b@kefaloscheese.com"], Assert.Single(_sent).To);
        Assert.Equal(EmailQueueStatus.Sent, (await SingleRowAsync()).Status);
    }

    [Fact]
    public async Task A_refused_send_is_tried_again_and_given_up_after_three_attempts()
    {
        _send = _ => Task.FromResult(new EmailSentResponseDto { Success = false, Message = "Mailbox unavailable" });
        await GivenRowAsync();

        var first = await Run();
        Assert.Equal(1, first.Retrying);
        var afterOne = await SingleRowAsync();
        Assert.Equal(EmailQueueStatus.Pending, afterOne.Status);
        Assert.Equal(1, afterOne.AttemptCount);
        Assert.Equal("Mailbox unavailable", afterOne.LastError);

        await Run();
        var last = await Run();

        Assert.Equal(1, last.Failed);
        var row = await SingleRowAsync();
        Assert.Equal(EmailQueueStatus.Failed, row.Status);
        Assert.Equal(3, row.AttemptCount);
        Assert.Equal(3, _sent.Count);

        // Given up means given up: a fourth pass leaves it alone.
        await Run();
        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public async Task A_send_that_throws_neither_strands_its_row_nor_stops_the_rest()
    {
        await GivenRowAsync(toAddresses: "[\"first@kefaloscheese.com\"]", createdAt: Now.UtcDateTime.AddMinutes(-2));
        await GivenRowAsync(toAddresses: "[\"second@kefaloscheese.com\"]", createdAt: Now.UtcDateTime.AddMinutes(-1));
        _send = request => request.To[0] == "first@kefaloscheese.com"
            ? Task.FromException<EmailSentResponseDto>(new InvalidOperationException("SMTP went away"))
            : Task.FromResult(new EmailSentResponseDto { Success = true });

        var result = await Run();

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.Retrying);
        var rows = await _context.EmailQueueItems.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(EmailQueueStatus.Pending, rows[0].Status);
        Assert.Contains("SMTP went away", rows[0].LastError);
        Assert.Equal(EmailQueueStatus.Sent, rows[1].Status);
    }

    [Fact]
    public async Task Nothing_is_attempted_while_email_is_switched_off()
    {
        // Otherwise every row would burn its three attempts on "Email service is disabled" and be
        // given up before anyone switched email on.
        await GivenRowAsync();

        var result = await Run(emailEnabled: false);

        Assert.Equal(0, result.Sent + result.Retrying + result.Failed + result.Expired);
        Assert.Empty(_sent);
        var row = await SingleRowAsync();
        Assert.Equal(EmailQueueStatus.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
    }

    [Fact]
    public async Task A_row_cut_off_mid_send_is_tried_again_but_one_still_sending_is_left_alone()
    {
        await GivenRowAsync(toAddresses: "[\"stuck@kefaloscheese.com\"]", status: EmailQueueStatus.Sending,
            attempts: 1, lastAttemptAt: Now.UtcDateTime.AddMinutes(-20));
        await GivenRowAsync(toAddresses: "[\"busy@kefaloscheese.com\"]", status: EmailQueueStatus.Sending,
            attempts: 1, lastAttemptAt: Now.UtcDateTime.AddMinutes(-2));

        await Run();

        Assert.Equal(["stuck@kefaloscheese.com"], Assert.Single(_sent).To);
        var rows = await _context.EmailQueueItems.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(EmailQueueStatus.Sent, rows[0].Status);
        Assert.Equal(2, rows[0].AttemptCount);
        Assert.Equal(EmailQueueStatus.Sending, rows[1].Status);
    }

    [Fact]
    public async Task A_row_stranded_in_sending_by_the_old_drain_is_recovered()
    {
        // The old drain set "Sending" and then threw, with no attempt time to go on.
        await GivenRowAsync(status: EmailQueueStatus.Sending, attempts: 1, createdAt: Now.UtcDateTime.AddHours(-1));

        await Run();

        Assert.Single(_sent);
        Assert.Equal(EmailQueueStatus.Sent, (await SingleRowAsync()).Status);
    }

    [Fact]
    public async Task A_row_more_than_a_day_old_is_dropped_rather_than_sent_late()
    {
        // A reset token lasts 24 hours; mailing one after that sends a link that no longer works.
        await GivenRowAsync(createdAt: Now.UtcDateTime.AddHours(-25));

        var result = await Run();

        Assert.Equal(1, result.Expired);
        Assert.Empty(_sent);
        var row = await SingleRowAsync();
        Assert.Equal(EmailQueueStatus.Failed, row.Status);
        Assert.Contains("24 hours", row.LastError);
    }

    // ---- Harness ------------------------------------------------------------------------------------

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    /// <summary>Each pass gets its own context, the way each Quartz firing gets a scope.</summary>
    private async Task<ProcessEmailQueueResult> Run(bool emailEnabled = true)
    {
        await using var context = NewContext();
        var email = StubProxy.For<IEmailService>((method, args) =>
        {
            if (method.Name == nameof(IEmailService.SendEmailAsync) && args!.Length == 2)
            {
                var request = (SendEmailRequest)args[0]!;
                _sent.Add(request);
                return _send(request);
            }

            throw new InvalidOperationException($"IEmailService.{method.Name} was not expected to be called.");
        });

        var handler = new ProcessEmailQueueHandler(
            context,
            email,
            Options.Create(new EmailSettings { Enabled = emailEnabled }),
            new FixedClock(Now),
            NullLogger<ProcessEmailQueueHandler>.Instance);

        var result = await handler.Handle(new ProcessEmailQueueCommand(), CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task GivenRowAsync(
        string toAddresses = "[\"someone@kefaloscheese.com\"]",
        string status = EmailQueueStatus.Pending,
        int attempts = 0,
        DateTime? createdAt = null,
        DateTime? lastAttemptAt = null)
    {
        await using var context = NewContext();
        context.EmailQueueItems.Add(new EmailQueueItem
        {
            ToAddresses = toAddresses,
            Subject = "Subject",
            Body = "Body",
            IsHtml = false,
            Status = status,
            AttemptCount = attempts,
            CreatedAt = createdAt ?? Now.UtcDateTime.AddMinutes(-1),
            LastAttemptAt = lastAttemptAt
        });
        await context.SaveChangesAsync();
    }

    private Task<EmailQueueItem> SingleRowAsync() => _context.EmailQueueItems.AsNoTracking().SingleAsync();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
