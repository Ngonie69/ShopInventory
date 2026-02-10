using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Options;
using ShopInventory.Web.Models;
using System.Globalization;

namespace ShopInventory.Web.Services;

public class EmailSettings
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public bool EnableSsl { get; set; }
    public string ApplicationUrl { get; set; } = string.Empty;
}

public interface IEmailService
{
    Task<bool> SendEmailAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null);
    Task<bool> SendInvoiceNotificationAsync(string toEmail, string toName, int invoiceDocEntry, string invoiceNumber, decimal totalAmount);
    Task<bool> SendCustomerInvoiceNotificationAsync(string toEmail, string toName, int invoiceDocEntry, string invoiceNumber, decimal totalAmount);
    Task<bool> SendPaymentReceivedAsync(string toEmail, string toName, string paymentReference, decimal amount);
    Task<bool> SendLowStockAlertAsync(string toEmail, string toName, List<LowStockItem> items);
    Task<bool> SendPasswordResetAsync(string toEmail, string toName, string resetToken);
    Task<bool> SendWelcomeEmailAsync(string toEmail, string toName, string username);
    Task<bool> SendStatementEmailAsync(string toEmail, string toName, CustomerStatementResponse statement, DateTime fromDate, DateTime toDate, string frequencyLabel);
}

public class LowStockItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal CurrentQuantity { get; set; }
    public decimal MinimumQuantity { get; set; }
}

public class EmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailSettings> settings, ILogger<EmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendEmailAsync(string toEmail, string toName, string subject, string htmlBody, string? textBody = null)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Email sending is disabled. Would have sent email to {ToEmail} with subject: {Subject}", toEmail, subject);
            return true;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            message.To.Add(new MailboxAddress(toName, toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = WrapInEmailTemplate(htmlBody, subject),
                TextBody = textBody ?? StripHtml(htmlBody)
            };

            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();

            var secureSocketOptions = _settings.EnableSsl
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.Auto;

            await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, secureSocketOptions);
            await client.AuthenticateAsync(_settings.SmtpUsername, _settings.SmtpPassword);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Email sent successfully to {ToEmail} with subject: {Subject}", toEmail, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {ToEmail} with subject: {Subject}", toEmail, subject);
            return false;
        }
    }

    public async Task<bool> SendInvoiceNotificationAsync(string toEmail, string toName, int invoiceDocEntry, string invoiceNumber, decimal totalAmount)
    {
        var subject = $"Invoice #{invoiceNumber} Created - Kefalos Workshop";
        var htmlBody = $@"
            <h2>Invoice Created</h2>
            <p>Dear {toName},</p>
            <p>A new invoice has been created for your account.</p>
            <table style='border-collapse: collapse; width: 100%; max-width: 400px;'>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Invoice Number</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>{invoiceNumber}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Total Amount</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>${totalAmount:N2}</td>
                </tr>
            </table>
            <p style='margin-top: 20px;'>
                <a href='{_settings.ApplicationUrl}/invoices/{invoiceDocEntry}' 
                   style='background: #4f46e5; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>
                   View Invoice
                </a>
            </p>
            <p>Thank you for your business!</p>";

        return await SendEmailAsync(toEmail, toName, subject, htmlBody);
    }

    public async Task<bool> SendCustomerInvoiceNotificationAsync(string toEmail, string toName, int invoiceDocEntry, string invoiceNumber, decimal totalAmount)
    {
        var subject = $"New Invoice #{invoiceNumber} - Kefalos Workshop";
        var portalUrl = $"{_settings.ApplicationUrl}/customer-portal/invoices?invoice={invoiceDocEntry}";

        var htmlBody = $@"
            <h2>New Invoice Available</h2>
            <p>Dear {toName},</p>
            <p>Your invoice is ready in the customer portal.</p>
            <table style='border-collapse: collapse; width: 100%; max-width: 400px;'>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Invoice Number</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>{invoiceNumber}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Total Amount</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>${totalAmount:N2}</td>
                </tr>
            </table>
            <p style='margin-top: 20px;'>
                <a href='{portalUrl}' 
                   style='background: #4f46e5; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>
                   View Invoices
                </a>
            </p>
            <p>Thank you for your business!</p>";

        return await SendEmailAsync(toEmail, toName, subject, htmlBody);
    }

    public async Task<bool> SendPaymentReceivedAsync(string toEmail, string toName, string paymentReference, decimal amount)
    {
        var subject = $"Payment Received - {paymentReference}";
        var htmlBody = $@"
            <h2>Payment Confirmation</h2>
            <p>Dear {toName},</p>
            <p>We have received your payment. Thank you!</p>
            <table style='border-collapse: collapse; width: 100%; max-width: 400px;'>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Reference</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>{paymentReference}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Amount</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>${amount:N2}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Date</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>{DateTime.Now:MMMM dd, yyyy}</td>
                </tr>
            </table>
            <p>If you have any questions, please contact us.</p>";

        return await SendEmailAsync(toEmail, toName, subject, htmlBody);
    }

    public async Task<bool> SendLowStockAlertAsync(string toEmail, string toName, List<LowStockItem> items)
    {
        var subject = $"Low Stock Alert - {items.Count} item(s) need attention";

        var itemsTable = string.Join("", items.Select(i => $@"
            <tr>
                <td style='padding: 8px; border: 1px solid #ddd;'>{i.ItemCode}</td>
                <td style='padding: 8px; border: 1px solid #ddd;'>{i.ItemName}</td>
                <td style='padding: 8px; border: 1px solid #ddd; color: #dc2626;'>{i.CurrentQuantity:N0}</td>
                <td style='padding: 8px; border: 1px solid #ddd;'>{i.MinimumQuantity:N0}</td>
            </tr>"));

        var htmlBody = $@"
            <h2 style='color: #dc2626;'>⚠️ Low Stock Alert</h2>
            <p>Dear {toName},</p>
            <p>The following items are running low on stock and need your attention:</p>
            <table style='border-collapse: collapse; width: 100%;'>
                <thead>
                    <tr style='background: #f5f5f5;'>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: left;'>Item Code</th>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: left;'>Item Name</th>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: left;'>Current Qty</th>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: left;'>Min Qty</th>
                    </tr>
                </thead>
                <tbody>
                    {itemsTable}
                </tbody>
            </table>
            <p style='margin-top: 20px;'>
                <a href='{_settings.ApplicationUrl}/products' 
                   style='background: #4f46e5; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>
                   View Products
                </a>
            </p>";

        return await SendEmailAsync(toEmail, toName, subject, htmlBody);
    }

    public async Task<bool> SendPasswordResetAsync(string toEmail, string toName, string resetToken)
    {
        var subject = "Password Reset Request - Kefalos Workshop";
        var resetUrl = $"{_settings.ApplicationUrl}/reset-password?token={Uri.EscapeDataString(resetToken)}";

        var htmlBody = $@"
            <h2>Password Reset Request</h2>
            <p>Dear {toName},</p>
            <p>We received a request to reset your password. Click the button below to proceed:</p>
            <p style='margin: 30px 0;'>
                <a href='{resetUrl}' 
                   style='background: #4f46e5; color: white; padding: 12px 24px; text-decoration: none; border-radius: 5px; font-weight: bold;'>
                   Reset Password
                </a>
            </p>
            <p>This link will expire in 24 hours.</p>
            <p style='color: #666; font-size: 14px;'>If you didn't request this password reset, please ignore this email or contact support if you have concerns.</p>
            <hr style='border: none; border-top: 1px solid #eee; margin: 20px 0;'>
            <p style='color: #999; font-size: 12px;'>If the button doesn't work, copy and paste this link into your browser:<br>{resetUrl}</p>";

        return await SendEmailAsync(toEmail, toName, subject, htmlBody);
    }

    public async Task<bool> SendWelcomeEmailAsync(string toEmail, string toName, string username)
    {
        var subject = "Welcome to Kefalos Workshop System";

        var htmlBody = $@"
            <h2>Welcome to Kefalos Workshop!</h2>
            <p>Dear {toName},</p>
            <p>Your account has been created successfully. Here are your login details:</p>
            <table style='border-collapse: collapse; width: 100%; max-width: 400px;'>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Username</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>{username}</td>
                </tr>
            </table>
            <p style='margin-top: 20px;'>
                <a href='{_settings.ApplicationUrl}/login' 
                   style='background: #4f46e5; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>
                   Login Now
                </a>
            </p>
            <p>If you have any questions, please contact your administrator.</p>";

        return await SendEmailAsync(toEmail, toName, subject, htmlBody);
    }

    public async Task<bool> SendStatementEmailAsync(
        string toEmail,
        string toName,
        CustomerStatementResponse statement,
        DateTime fromDate,
        DateTime toDate,
        string frequencyLabel)
    {
        var periodLabel = $"{FormatDate(fromDate)} - {FormatDate(toDate)}";
        var subject = $"{frequencyLabel} Statement - {periodLabel}";
        var htmlBody = BuildStatementEmailBody(toName, statement, fromDate, toDate, frequencyLabel);

        return await SendEmailAsync(toEmail, toName, subject, htmlBody);
    }

    private string WrapInEmailTemplate(string content, string title)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>{title}</title>
</head>
<body style='margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, ""Helvetica Neue"", Arial, sans-serif; background-color: #f5f5f5;'>
    <table width='100%' cellpadding='0' cellspacing='0' style='background-color: #f5f5f5; padding: 20px 0;'>
        <tr>
            <td align='center'>
                <table width='600' cellpadding='0' cellspacing='0' style='background-color: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 4px rgba(0,0,0,0.1);'>
                    <!-- Header -->
                    <tr>
                        <td style='background: linear-gradient(135deg, #4f46e5, #7c3aed); padding: 30px; text-align: center;'>
                            <h1 style='margin: 0; color: #ffffff; font-size: 24px;'>Kefalos Workshop</h1>
                        </td>
                    </tr>
                    <!-- Content -->
                    <tr>
                        <td style='padding: 30px;'>
                            {content}
                        </td>
                    </tr>
                    <!-- Footer -->
                    <tr>
                        <td style='background-color: #f8fafc; padding: 20px 30px; text-align: center; border-top: 1px solid #e2e8f0;'>
                            <p style='margin: 0; color: #64748b; font-size: 12px;'>
                                © {DateTime.Now.Year} Kefalos Workshop System. All rights reserved.
                            </p>
                            <p style='margin: 10px 0 0 0; color: #94a3b8; font-size: 11px;'>
                                This is an automated message. Please do not reply directly to this email.
                            </p>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
    }

    private string BuildStatementEmailBody(
        string toName,
        CustomerStatementResponse statement,
        DateTime fromDate,
        DateTime toDate,
        string frequencyLabel)
    {
        var currency = statement.Customer.Currency;
        var periodLabel = $"{FormatDate(fromDate)} - {FormatDate(toDate)}";
        var statementUrl = $"{_settings.ApplicationUrl}/customer-portal/statements";
        var recentLines = statement.Lines
            .OrderByDescending(l => l.Date)
            .Take(10)
            .ToList();

        var lineRows = recentLines.Any()
            ? string.Join("", recentLines.Select(line => $@"
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd;'>{FormatDate(line.Date)}</td>
                    <td style='padding: 8px; border: 1px solid #ddd;'>{line.Description ?? line.DocumentType}</td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{FormatAmount(line.Debit, currency)}</td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{FormatAmount(line.Credit, currency)}</td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{FormatAmount(line.Balance, currency, true)}</td>
                </tr>"))
            : "<tr><td colspan='5' style='padding: 12px; border: 1px solid #ddd; text-align: center; color: #666;'>No transactions recorded for this period.</td></tr>";

        return $@"
            <h2>{frequencyLabel} Statement</h2>
            <p>Dear {toName},</p>
            <p>Here is your account statement for <strong>{periodLabel}</strong>.</p>
            <table style='border-collapse: collapse; width: 100%; max-width: 520px;'>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Opening Balance</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{FormatAmount(statement.OpeningBalance, currency, true)}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Total Invoices</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{FormatAmount(statement.TotalInvoices, currency, true)}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Total Payments</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{FormatAmount(statement.TotalPayments, currency, true)}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Total Credit Notes</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right;'>{FormatAmount(statement.TotalCreditNotes, currency, true)}</td>
                </tr>
                <tr>
                    <td style='padding: 8px; border: 1px solid #ddd; background: #f5f5f5;'><strong>Closing Balance</strong></td>
                    <td style='padding: 8px; border: 1px solid #ddd; text-align: right; font-weight: bold;'>{FormatAmount(statement.ClosingBalance, currency, true)}</td>
                </tr>
            </table>

            <h3 style='margin-top: 24px;'>Recent Activity</h3>
            <table style='border-collapse: collapse; width: 100%;'>
                <thead>
                    <tr style='background: #f5f5f5;'>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: left;'>Date</th>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: left;'>Description</th>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: right;'>Debit</th>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: right;'>Credit</th>
                        <th style='padding: 10px; border: 1px solid #ddd; text-align: right;'>Balance</th>
                    </tr>
                </thead>
                <tbody>
                    {lineRows}
                </tbody>
            </table>

            <p style='margin-top: 20px;'>
                <a href='{statementUrl}' 
                   style='background: #4f46e5; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px;'>
                   View Statement Online
                </a>
            </p>
            <p>If you have any questions, please contact support.</p>";
    }

    private static string FormatAmount(decimal amount, string? currency, bool showZero = false)
    {
        if (!showZero && amount == 0m)
        {
            return "-";
        }

        var formatted = amount.ToString("N2", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(currency) ? formatted : $"{formatted} {currency}";
    }

    private static string FormatDate(DateTime date)
    {
        return date.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        // Simple HTML stripping - for production, consider using HtmlAgilityPack
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }
}
