using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Service interface for queueing emails
/// </summary>
public interface IEmailQueueService
{
    /// <summary>
    /// Queue an email for sending
    /// </summary>
    Task QueueEmailAsync(string to, string subject, string body, string? category = null);

    /// <summary>
    /// Process queued emails (called by background service)
    /// </summary>
    Task ProcessQueueAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of email queue service
/// </summary>
public class EmailQueueService : IEmailQueueService
{
    private readonly ApplicationDbContext _context;
    private readonly IEmailService? _emailService;
    private readonly ILogger<EmailQueueService> _logger;

    public EmailQueueService(
        ApplicationDbContext context,
        ILogger<EmailQueueService> logger,
        IEmailService? emailService = null)
    {
        _context = context;
        _logger = logger;
        _emailService = emailService;
    }

    public async Task QueueEmailAsync(string to, string subject, string body, string? category = null)
    {
        var emailItem = new EmailQueueItem
        {
            ToAddresses = to,
            Subject = subject,
            Body = body,
            IsHtml = false,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            AttemptCount = 0
        };

        _context.EmailQueueItems.Add(emailItem);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Email queued: To={To}, Subject={Subject}", to, subject);
    }

    public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        if (_emailService == null)
        {
            _logger.LogWarning("Email service is not configured. Emails will remain in queue.");
            return;
        }

        var pendingEmails = await _context.EmailQueueItems
            .Where(e => e.Status == "Pending" || (e.Status == "Failed" && e.AttemptCount < 3))
            .OrderBy(e => e.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var email in pendingEmails)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var request = new SendEmailRequest
                {
                    To = email.ToAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    Subject = email.Subject,
                    Body = email.Body,
                    IsHtml = email.IsHtml
                };
                var result = await _emailService.SendEmailAsync(request, cancellationToken);

                if (result.Success)
                {
                    email.Status = "Sent";
                    email.SentAt = DateTime.UtcNow;
                    _logger.LogInformation("Email sent: Id={Id}, To={To}", email.Id, email.ToAddresses);
                }
                else
                {
                    email.Status = "Failed";
                    email.AttemptCount++;
                    email.LastError = result.Message ?? "Email service returned failure";
                    _logger.LogWarning("Email send failed: Id={Id}, To={To}", email.Id, email.ToAddresses);
                }
            }
            catch (Exception ex)
            {
                email.Status = "Failed";
                email.AttemptCount++;
                email.LastError = ex.Message;
                _logger.LogError(ex, "Error sending email: Id={Id}, To={To}", email.Id, email.ToAddresses);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
