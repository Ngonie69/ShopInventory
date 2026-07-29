using System.Text;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Features.DesktopIntegration.Queries.GenerateEndOfDayReport;

namespace ShopInventory.Services;

/// <summary>
/// Runs end-of-day consolidation and emails the report. Exposed as a scoped service so it can be
/// invoked both by <see cref="EndOfDayConsolidationJob"/> (on the Quartz cron schedule) and
/// on demand from DesktopIntegrationController's "run now" endpoint.
/// </summary>
public class EndOfDayConsolidationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EndOfDayConsolidationService> _logger;
    private readonly DailyStockSettings _settings;

    public EndOfDayConsolidationService(
        IServiceProvider serviceProvider,
        IOptions<DailyStockSettings> settings,
        ILogger<EndOfDayConsolidationService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task RunConsolidationAndReportAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        _logger.LogInformation("Starting end-of-day consolidation for {Date}", today);

        using var scope = _serviceProvider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Step 1: Consolidate sales
        var consolidationResult = await mediator.Send(
            new ConsolidateDailySalesCommand(today), ct);

        if (consolidationResult.IsError)
        {
            _logger.LogWarning("Consolidation returned errors: {Errors}",
                string.Join(", ", consolidationResult.Errors.Select(e => e.Description)));
        }
        else
        {
            var result = consolidationResult.Value;
            _logger.LogInformation(
                "Consolidation complete: {Total} sales processed, {Success} posted, {Failed} failed",
                result.TotalSalesProcessed, result.SuccessfulPostings, result.FailedPostings);
        }

        // Step 2: Generate report
        var reportResult = await mediator.Send(
            new GenerateEndOfDayReportQuery(today), ct);

        if (reportResult.IsError)
        {
            _logger.LogWarning("Report generation failed: {Errors}",
                string.Join(", ", reportResult.Errors.Select(e => e.Description)));
            return;
        }

        // Step 3: Email report
        await SendReportEmailAsync(scope.ServiceProvider, reportResult.Value, ct);
    }

    private async Task SendReportEmailAsync(
        IServiceProvider services, EndOfDayReportDto report, CancellationToken ct)
    {
        if (_settings.ReportRecipients.Count == 0)
        {
            _logger.LogWarning("No report recipients configured — skipping email");
            return;
        }

        var emailService = services.GetRequiredService<IEmailService>();
        var subject = $"End of Day Sales Report — {report.ReportDate:dd MMM yyyy}";
        var body = BuildReportHtml(report);

        foreach (var recipient in _settings.ReportRecipients)
        {
            try
            {
                await emailService.SendEmailAsync(recipient, subject, body, cancellationToken: ct);
                _logger.LogInformation("End-of-day report sent to {Recipient}", recipient);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send end-of-day report to {Recipient}", recipient);
            }
        }
    }

    private static string BuildReportHtml(EndOfDayReportDto report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<html><body style='font-family: Arial, sans-serif;'>");
        sb.AppendLine($"<h2>End of Day Sales Report — {report.ReportDate:dd MMM yyyy}</h2>");
        sb.AppendLine($"<p>Generated: {report.GeneratedAt.AddHours(2):dd MMM yyyy HH:mm} CAT</p>");

        // Summary
        sb.AppendLine("<h3>Summary</h3>");
        sb.AppendLine("<table border='1' cellpadding='8' cellspacing='0' style='border-collapse: collapse;'>");
        sb.AppendLine("<tr><td><strong>Total Sales</strong></td><td>{0}</td></tr>".Replace("{0}", report.TotalSalesCount.ToString()));
        sb.AppendLine("<tr><td><strong>Total Amount</strong></td><td>{0:N2}</td></tr>".Replace("{0:N2}", report.TotalSalesAmount.ToString("N2")));
        sb.AppendLine("<tr><td><strong>Total VAT</strong></td><td>{0:N2}</td></tr>".Replace("{0:N2}", report.TotalVatAmount.ToString("N2")));
        sb.AppendLine("<tr><td><strong>Total Paid</strong></td><td>{0:N2}</td></tr>".Replace("{0:N2}", report.TotalAmountPaid.ToString("N2")));
        sb.AppendLine("<tr><td><strong>Posted Invoices</strong></td><td>{0}</td></tr>".Replace("{0}", report.PostedInvoiceCount.ToString()));
        sb.AppendLine("<tr><td><strong>Unposted Invoices</strong></td><td style='color: {1};'>{0}</td></tr>"
            .Replace("{0}", report.UnpostedInvoiceCount.ToString())
            .Replace("{1}", report.UnpostedInvoiceCount > 0 ? "red" : "green"));
        sb.AppendLine("</table>");

        // BP Summaries
        sb.AppendLine("<h3>Business Partner Summary</h3>");
        sb.AppendLine("<table border='1' cellpadding='8' cellspacing='0' style='border-collapse: collapse;'>");
        sb.AppendLine("<tr style='background: #f0f0f0;'><th>Customer</th><th>Sales</th><th>Amount</th><th>Paid</th><th>SAP Invoice</th><th>Payment</th></tr>");

        foreach (var bp in report.BusinessPartnerSummaries)
        {
            var invoiceInfo = bp.ConsolidatedInvoice?.SapDocNum?.ToString() ?? "<em>Not posted</em>";
            var paymentInfo = bp.IncomingPayment?.SapDocNum?.ToString() ?? "-";

            sb.AppendLine($"<tr><td>{bp.CardCode} — {bp.CardName}</td><td>{bp.SalesCount}</td>"
                + $"<td>{bp.TotalAmount:N2}</td><td>{bp.TotalPaid:N2}</td>"
                + $"<td>{invoiceInfo}</td><td>{paymentInfo}</td></tr>");
        }
        sb.AppendLine("</table>");

        // Unposted sales (highlighted)
        if (report.UnpostedSales.Count > 0)
        {
            sb.AppendLine("<h3 style='color: red;'>⚠ Unposted Sales</h3>");
            sb.AppendLine("<table border='1' cellpadding='8' cellspacing='0' style='border-collapse: collapse;'>");
            sb.AppendLine("<tr style='background: #ffe0e0;'><th>Reference</th><th>Customer</th><th>Amount</th><th>Fiscal Receipt</th><th>Status</th><th>Reason</th></tr>");

            foreach (var sale in report.UnpostedSales)
            {
                sb.AppendLine($"<tr><td>{sale.ExternalReferenceId}</td><td>{sale.CardCode} — {sale.CardName}</td>"
                    + $"<td>{sale.Amount:N2}</td><td>{sale.FiscalReceiptNumber ?? "-"}</td>"
                    + $"<td>{sale.ConsolidationStatus}</td><td>{sale.Reason}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        // Individual sales detail per BP
        sb.AppendLine("<h3>Detailed Sales by Business Partner</h3>");
        foreach (var bp in report.BusinessPartnerSummaries)
        {
            sb.AppendLine($"<h4>{bp.CardCode} — {bp.CardName}</h4>");
            sb.AppendLine("<table border='1' cellpadding='6' cellspacing='0' style='border-collapse: collapse; font-size: 0.9em;'>");
            sb.AppendLine("<tr style='background: #f8f8f8;'><th>Reference</th><th>Amount</th><th>VAT</th><th>Fiscal Receipt</th><th>Payment</th><th>Time</th></tr>");

            foreach (var sale in bp.IndividualSales)
            {
                sb.AppendLine($"<tr><td>{sale.ExternalReferenceId}</td><td>{sale.Amount:N2}</td>"
                    + $"<td>{sale.VatAmount:N2}</td><td>{sale.FiscalReceiptNumber ?? "-"}</td>"
                    + $"<td>{sale.PaymentMethod}: {sale.AmountPaid:N2}</td>"
                    + $"<td>{sale.CreatedAt.AddHours(2):HH:mm}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}

/// <summary>
/// Quartz job that triggers <see cref="EndOfDayConsolidationService"/> on the configured CAT cron
/// schedule (DailyStock:EndOfDayTimeCAT, gated by DailyStock:EnableAutoConsolidation).
/// </summary>
[DisallowConcurrentExecution]
public sealed class EndOfDayConsolidationJob : IJob
{
    private readonly EndOfDayConsolidationService _consolidationService;

    public EndOfDayConsolidationJob(EndOfDayConsolidationService consolidationService)
    {
        _consolidationService = consolidationService;
    }

    public Task Execute(IJobExecutionContext context)
    {
        return _consolidationService.RunConsolidationAndReportAsync(context.CancellationToken);
    }
}
