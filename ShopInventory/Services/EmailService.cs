using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Email settings configuration
/// </summary>
public class EmailSettings
{
    public string SmtpServer { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Shop Inventory";
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Interface for email service
/// </summary>
public interface IEmailService
{
    Task<EmailSentResponseDto> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default);
    Task<EmailSentResponseDto> SendEmailFromTemplateAsync(string templateName, Dictionary<string, string> parameters, List<string> to, string subject, CancellationToken cancellationToken = default);
    Task QueueEmailAsync(SendEmailRequest request, string? category = null, CancellationToken cancellationToken = default);
    Task ProcessEmailQueueAsync(CancellationToken cancellationToken = default);
    Task<EmailSentResponseDto> TestEmailConfigurationAsync(string toEmail, CancellationToken cancellationToken = default);
}

/// <summary>
/// Email service implementation
/// </summary>
public class EmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<EmailService> _logger;

    public EmailService(
        IOptions<EmailSettings> settings,
        ApplicationDbContext context,
        ILogger<EmailService> logger)
    {
        _settings = settings.Value;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Send an email immediately
    /// </summary>
    public async Task<EmailSentResponseDto> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            _logger.LogWarning("Email service is disabled. Email not sent to: {Recipients}", string.Join(", ", request.To));
            return new EmailSentResponseDto
            {
                Success = false,
                Message = "Email service is disabled",
                SentAt = DateTime.UtcNow
            };
        }

        try
        {
            using var client = new SmtpClient(_settings.SmtpServer, _settings.SmtpPort)
            {
                EnableSsl = _settings.UseSsl,
                Credentials = new NetworkCredential(_settings.Username, _settings.Password)
            };

            var message = new MailMessage
            {
                From = new MailAddress(_settings.FromEmail, _settings.FromName),
                Subject = request.Subject,
                Body = request.Body,
                IsBodyHtml = request.IsHtml
            };

            foreach (var to in request.To)
            {
                message.To.Add(to);
            }

            if (request.Cc != null)
            {
                foreach (var cc in request.Cc)
                {
                    message.CC.Add(cc);
                }
            }

            if (request.Bcc != null)
            {
                foreach (var bcc in request.Bcc)
                {
                    message.Bcc.Add(bcc);
                }
            }

            await client.SendMailAsync(message, cancellationToken);

            _logger.LogInformation("Email sent successfully to: {Recipients}", string.Join(", ", request.To));

            return new EmailSentResponseDto
            {
                Success = true,
                Message = "Email sent successfully",
                MessageId = Guid.NewGuid().ToString(),
                SentAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to: {Recipients}", string.Join(", ", request.To));
            return new EmailSentResponseDto
            {
                Success = false,
                Message = $"Failed to send email: {ex.Message}",
                SentAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Send email from a template
    /// </summary>
    public async Task<EmailSentResponseDto> SendEmailFromTemplateAsync(string templateName, Dictionary<string, string> parameters, List<string> to, string subject, CancellationToken cancellationToken = default)
    {
        var body = GetEmailTemplate(templateName);

        foreach (var param in parameters)
        {
            body = body.Replace($"{{{{{param.Key}}}}}", param.Value);
        }

        return await SendEmailAsync(new SendEmailRequest
        {
            To = to,
            Subject = subject,
            Body = body,
            IsHtml = true
        }, cancellationToken);
    }

    /// <summary>
    /// Queue an email for later sending
    /// </summary>
    public async Task QueueEmailAsync(SendEmailRequest request, string? category = null, CancellationToken cancellationToken = default)
    {
        var queueItem = new EmailQueueItem
        {
            ToAddresses = JsonSerializer.Serialize(request.To),
            CcAddresses = request.Cc != null ? JsonSerializer.Serialize(request.Cc) : null,
            BccAddresses = request.Bcc != null ? JsonSerializer.Serialize(request.Bcc) : null,
            Subject = request.Subject,
            Body = request.Body,
            IsHtml = request.IsHtml,
            Status = "Pending",
            Category = category,
            CreatedAt = DateTime.UtcNow
        };

        _context.EmailQueueItems.Add(queueItem);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Email queued for: {Recipients}", string.Join(", ", request.To));
    }

    /// <summary>
    /// Process pending emails in the queue
    /// </summary>
    public async Task ProcessEmailQueueAsync(CancellationToken cancellationToken = default)
    {
        var pendingEmails = await _context.EmailQueueItems
            .Where(e => e.Status == "Pending" && e.AttemptCount < 3)
            .OrderBy(e => e.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var email in pendingEmails)
        {
            email.Status = "Sending";
            email.AttemptCount++;
            await _context.SaveChangesAsync(cancellationToken);

            var request = new SendEmailRequest
            {
                To = JsonSerializer.Deserialize<List<string>>(email.ToAddresses) ?? new List<string>(),
                Cc = !string.IsNullOrEmpty(email.CcAddresses) ? JsonSerializer.Deserialize<List<string>>(email.CcAddresses) : null,
                Bcc = !string.IsNullOrEmpty(email.BccAddresses) ? JsonSerializer.Deserialize<List<string>>(email.BccAddresses) : null,
                Subject = email.Subject,
                Body = email.Body,
                IsHtml = email.IsHtml
            };

            var result = await SendEmailAsync(request, cancellationToken);

            if (result.Success)
            {
                email.Status = "Sent";
                email.SentAt = DateTime.UtcNow;
            }
            else
            {
                email.Status = email.AttemptCount >= 3 ? "Failed" : "Pending";
                email.LastError = result.Message;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Test email configuration
    /// </summary>
    public async Task<EmailSentResponseDto> TestEmailConfigurationAsync(string toEmail, CancellationToken cancellationToken = default)
    {
        return await SendEmailAsync(new SendEmailRequest
        {
            To = new List<string> { toEmail },
            Subject = "Test Email from Shop Inventory",
            Body = GetTestEmailTemplate(),
            IsHtml = true
        }, cancellationToken);
    }

    private static string GetEmailTemplate(string templateName)
    {
        return templateName switch
        {
            "LowStockAlert" => @"
<!DOCTYPE html>
<html>
<head><style>
    body { font-family: Arial, sans-serif; margin: 0; padding: 20px; background-color: #f4f4f4; }
    .container { max-width: 600px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; }
    .header { background: #dc3545; color: white; padding: 15px; border-radius: 8px 8px 0 0; margin: -20px -20px 20px; }
    .alert-item { background: #fff3cd; border-left: 4px solid #ffc107; padding: 10px; margin: 10px 0; }
    .critical { background: #f8d7da; border-left-color: #dc3545; }
</style></head>
<body>
<div class='container'>
    <div class='header'><h2>⚠️ Low Stock Alert</h2></div>
    <p>The following items are running low on stock:</p>
    {{Items}}
    <p style='margin-top: 20px;'>Please review and reorder as necessary.</p>
    <p>Shop Inventory System</p>
</div>
</body>
</html>",
            "PaymentReceived" => @"
<!DOCTYPE html>
<html>
<head><style>
    body { font-family: Arial, sans-serif; margin: 0; padding: 20px; background-color: #f4f4f4; }
    .container { max-width: 600px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; }
    .header { background: #28a745; color: white; padding: 15px; border-radius: 8px 8px 0 0; margin: -20px -20px 20px; }
</style></head>
<body>
<div class='container'>
    <div class='header'><h2>💰 Payment Received</h2></div>
    <p>A payment has been received:</p>
    <ul>
        <li><strong>Customer:</strong> {{CustomerName}}</li>
        <li><strong>Amount:</strong> {{Amount}} {{Currency}}</li>
        <li><strong>Payment Method:</strong> {{PaymentMethod}}</li>
        <li><strong>Reference:</strong> {{Reference}}</li>
    </ul>
    <p>Shop Inventory System</p>
</div>
</body>
</html>",
            _ => "<html><body><p>{{Content}}</p></body></html>"
        };
    }

    private static string GetTestEmailTemplate()
    {
        return @"
<!DOCTYPE html>
<html>
<head><style>
    body { font-family: Arial, sans-serif; margin: 0; padding: 20px; background-color: #f4f4f4; }
    .container { max-width: 600px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }
    .header { background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 20px; border-radius: 8px 8px 0 0; margin: -20px -20px 20px; text-align: center; }
    .success { color: #28a745; }
</style></head>
<body>
<div class='container'>
    <div class='header'><h2>✅ Email Configuration Test</h2></div>
    <p class='success'><strong>Success!</strong> Your email configuration is working correctly.</p>
    <p>This is a test email from the Shop Inventory system to verify that email notifications are properly configured.</p>
    <hr>
    <p style='color: #666; font-size: 12px;'>Sent at: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC") + @"</p>
</div>
</body>
</html>";
    }
}
