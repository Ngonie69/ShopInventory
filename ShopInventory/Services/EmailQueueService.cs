using ShopInventory.Data;
using ShopInventory.Features.Email;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Service interface for queueing emails
/// </summary>
public interface IEmailQueueService
{
    /// <summary>
    /// Queue a plain-text email for sending. <see cref="EmailQueueJob"/> sends it within a minute or so.
    /// </summary>
    Task QueueEmailAsync(string to, string subject, string body, string? category = null);
}

/// <summary>
/// Writes plain-text emails to the email queue, in the same shape as
/// <see cref="IEmailService.QueueEmailAsync"/>, so the one queue drain reads both.
/// </summary>
public class EmailQueueService : IEmailQueueService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<EmailQueueService> _logger;

    public EmailQueueService(
        ApplicationDbContext context,
        ILogger<EmailQueueService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task QueueEmailAsync(string to, string subject, string body, string? category = null)
    {
        var emailItem = new EmailQueueItem
        {
            ToAddresses = EmailQueueAddresses.Serialize(EmailQueueAddresses.Parse(to)),
            Subject = subject,
            Body = body,
            IsHtml = false,
            Status = EmailQueueStatus.Pending,
            Category = category,
            CreatedAt = DateTime.UtcNow,
            AttemptCount = 0
        };

        _context.EmailQueueItems.Add(emailItem);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Email queued: To={To}, Subject={Subject}", to, subject);
    }
}
