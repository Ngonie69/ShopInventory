using System.Globalization;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that polls the health-check subsystem and emails alerts when the system
/// transitions to Degraded or Unhealthy. A cooldown prevents alert storms; recovery is
/// acknowledged with a single "all-clear" email. Cooldown/last-status state is persisted in
/// the job's JobDataMap so it survives the per-execution job lifetime and is shared across the
/// cluster. The interval (SystemHealthAlert:CheckIntervalMinutes) and enablement are applied on
/// the trigger in QuartzConfiguration.
/// </summary>
[DisallowConcurrentExecution]
[PersistJobDataAfterExecution]
public sealed class SystemFailureAlertJob : IJob
{
    private const string LastNotifiedStatusKey = "lastNotifiedStatus";
    private const string LastAlertSentUtcKey = "lastAlertSentAtUtc";

    private readonly IServiceProvider _serviceProvider;
    private readonly SystemHealthAlertSettings _alertSettings;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<SystemFailureAlertJob> _logger;

    public SystemFailureAlertJob(
        IServiceProvider serviceProvider,
        IOptions<SystemHealthAlertSettings> alertSettings,
        IOptions<EmailSettings> emailSettings,
        ILogger<SystemFailureAlertJob> logger)
    {
        _serviceProvider = serviceProvider;
        _alertSettings = alertSettings.Value;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_alertSettings.Enabled || _alertSettings.AlertRecipients.Count == 0)
        {
            return;
        }

        var dataMap = context.JobDetail.JobDataMap;
        var lastNotifiedStatus = Enum.TryParse<HealthStatus>(dataMap.GetString(LastNotifiedStatusKey), out var parsedStatus)
            ? parsedStatus
            : HealthStatus.Healthy;
        var lastAlertSentAtUtc = DateTime.TryParse(
            dataMap.GetString(LastAlertSentUtcKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTime)
            ? parsedTime
            : DateTime.MinValue;

        await using var scope = _serviceProvider.CreateAsyncScope();
        var healthService = scope.ServiceProvider.GetRequiredService<HealthCheckService>();

        var report = await healthService.CheckHealthAsync(
            registration => registration.Tags.Contains("dependencies") || registration.Tags.Contains("ready"),
            context.CancellationToken);

        var currentStatus = report.Status;

        // Recovery: was bad, now healthy — send all-clear once
        if (lastNotifiedStatus != HealthStatus.Healthy && currentStatus == HealthStatus.Healthy)
        {
            _logger.LogInformation("System health recovered to Healthy — sending all-clear email");
            await SendEmailAsync(BuildRecoveryEmail(report), context.CancellationToken);
            dataMap.Put(LastNotifiedStatusKey, HealthStatus.Healthy.ToString());
            dataMap.Put(LastAlertSentUtcKey, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            return;
        }

        // Only alert for Degraded or Unhealthy
        if (currentStatus == HealthStatus.Healthy)
        {
            return;
        }

        // Enforce cooldown: don't spam
        var cooldown = TimeSpan.FromMinutes(_alertSettings.AlertCooldownMinutes);
        if (DateTime.UtcNow - lastAlertSentAtUtc < cooldown)
        {
            return;
        }

        // Send alert
        _logger.LogWarning("System health is {Status} — sending failure alert email", currentStatus);
        await SendEmailAsync(BuildAlertEmail(report, currentStatus), context.CancellationToken);
        dataMap.Put(LastNotifiedStatusKey, currentStatus.ToString());
        dataMap.Put(LastAlertSentUtcKey, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    private async Task SendEmailAsync((string subject, string body) email, CancellationToken cancellationToken)
    {
        if (!_emailSettings.Enabled)
        {
            _logger.LogWarning("Email is disabled — system health alert not sent");
            return;
        }

        try
        {
            using var client = new SmtpClient(_emailSettings.SmtpServer, _emailSettings.SmtpPort)
            {
                EnableSsl = _emailSettings.UseSsl,
                Credentials = new System.Net.NetworkCredential(_emailSettings.Username, _emailSettings.Password)
            };

            var message = new MailMessage
            {
                From = new MailAddress(_emailSettings.FromEmail, _emailSettings.FromName),
                Subject = email.subject,
                Body = email.body,
                IsBodyHtml = true
            };

            foreach (var recipient in _alertSettings.AlertRecipients)
                message.To.Add(recipient);

            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("System health alert email sent to {Recipients}", string.Join(", ", _alertSettings.AlertRecipients));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send system health alert email");
        }
    }

    private static (string subject, string body) BuildAlertEmail(HealthReport report, HealthStatus status)
    {
        var isUnhealthy = status == HealthStatus.Unhealthy;
        var subject = isUnhealthy
            ? $"🔴 SYSTEM FAILURE — Shop Inventory ({DateTime.UtcNow.AddHours(2):HH:mm} CAT)"
            : $"🟡 SYSTEM DEGRADED — Shop Inventory ({DateTime.UtcNow.AddHours(2):HH:mm} CAT)";

        var headerColor = isUnhealthy ? "#dc2626" : "#d97706";
        var headerLabel = isUnhealthy ? "SYSTEM FAILURE" : "SYSTEM DEGRADED";

        var sb = new StringBuilder();
        sb.Append($@"<!DOCTYPE html>
<html>
<head><style>
  body {{ font-family: Arial, sans-serif; background: #f4f4f4; margin: 0; padding: 20px; }}
  .wrap {{ max-width: 640px; margin: 0 auto; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,.12); }}
  .hdr  {{ background: {headerColor}; color: #fff; padding: 20px 24px; }}
  .hdr h2 {{ margin: 0; font-size: 1.2rem; }}
  .hdr p  {{ margin: 4px 0 0; font-size: .85rem; opacity: .85; }}
  .body {{ padding: 20px 24px; }}
  table {{ width: 100%; border-collapse: collapse; margin-top: 12px; }}
  th {{ text-align: left; background: #f9fafb; padding: 8px 10px; font-size: .8rem; color: #374151; border-bottom: 1px solid #e5e7eb; }}
  td {{ padding: 8px 10px; font-size: .85rem; border-bottom: 1px solid #f3f4f6; vertical-align: top; }}
  .pill-red  {{ display: inline-block; background: #fee2e2; color: #b91c1c; border-radius: 999px; padding: 1px 10px; font-size: .75rem; font-weight: 600; }}
  .pill-amber {{ display: inline-block; background: #fef3c7; color: #92400e; border-radius: 999px; padding: 1px 10px; font-size: .75rem; font-weight: 600; }}
  .ftr {{ padding: 12px 24px; background: #f9fafb; font-size: .75rem; color: #6b7280; border-top: 1px solid #e5e7eb; }}
</style></head>
<body><div class=""wrap"">
  <div class=""hdr""><h2>⚠ {headerLabel}</h2>
    <p>Detected at {DateTime.UtcNow.AddHours(2):dd MMM yyyy HH:mm} CAT ({DateTime.UtcNow:HH:mm} UTC)</p>
  </div>
  <div class=""body"">
    <p>One or more system components are reporting failures. Immediate operator attention may be required.</p>
    <table>
      <tr><th>Check</th><th>Status</th><th>Detail</th></tr>");

        foreach (var entry in report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy).OrderByDescending(e => e.Value.Status))
        {
            var pill = entry.Value.Status == HealthStatus.Unhealthy ? "pill-red" : "pill-amber";
            var desc = entry.Value.Description ?? string.Empty;
            sb.Append($"<tr><td>{entry.Key}</td><td><span class=\"{pill}\">{entry.Value.Status}</span></td><td>{System.Net.WebUtility.HtmlEncode(desc)}</td></tr>");
        }

        sb.Append($@"
    </table>
  </div>
  <div class=""ftr"">This is an automated alert from Shop Inventory. Do not reply to this email.</div>
</div></body></html>");

        return (subject, sb.ToString());
    }

    private static (string subject, string body) BuildRecoveryEmail(HealthReport report)
    {
        var subject = $"✅ System Recovered — Shop Inventory ({DateTime.UtcNow.AddHours(2):HH:mm} CAT)";
        var body = $@"<!DOCTYPE html>
<html>
<head><style>
  body {{ font-family: Arial, sans-serif; background: #f4f4f4; margin: 0; padding: 20px; }}
  .wrap {{ max-width: 640px; margin: 0 auto; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,.12); }}
  .hdr  {{ background: #16a34a; color: #fff; padding: 20px 24px; }}
  .hdr h2 {{ margin: 0; font-size: 1.2rem; }}
  .hdr p  {{ margin: 4px 0 0; font-size: .85rem; opacity: .85; }}
  .body {{ padding: 20px 24px; font-size: .9rem; }}
  .ftr {{ padding: 12px 24px; background: #f9fafb; font-size: .75rem; color: #6b7280; border-top: 1px solid #e5e7eb; }}
</style></head>
<body><div class=""wrap"">
  <div class=""hdr""><h2>✅ ALL SYSTEMS HEALTHY</h2>
    <p>Recovered at {DateTime.UtcNow.AddHours(2):dd MMM yyyy HH:mm} CAT ({DateTime.UtcNow:HH:mm} UTC)</p>
  </div>
  <div class=""body"">
    <p>All monitored system components have returned to a <strong>Healthy</strong> state. No further action is required.</p>
  </div>
  <div class=""ftr"">This is an automated alert from Shop Inventory. Do not reply to this email.</div>
</div></body></html>";

        return (subject, body);
    }
}
