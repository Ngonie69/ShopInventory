using System.Net;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class MerchandiserPurchaseOrderReport : IAsyncDisposable
{
    private const int OrdersPerPage = 10;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private IReportExportService ExportService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<MerchandiserPurchaseOrderReport> Logger { get; set; } = default!;

    private GetMerchandiserPurchaseOrderReportResult reportResult = new();
    private MerchandiserPurchaseOrderReportOrderModel? selectedOrder;
    private bool isLoading = true;
    private bool isExporting;
    private bool hasInitialized;
    private bool hasLoggedView;
    private bool showAttachmentViewer;
    private bool isLoadingAttachmentViewer;
    private string? attachmentViewerDataUri;
    private string? attachmentViewerFileName;
    private string? attachmentViewerMimeType;
    private MerchandiserPurchaseOrderReportAttachmentModel? attachmentViewerAttachment;
    private string? errorMessage;
    private string searchTerm = string.Empty;
    private string selectedMerchandiserValue = string.Empty;
    private string attachmentFilter = "all";
    private DateTime? fromDate = DateTime.Today.AddDays(-30);
    private DateTime? toDate = DateTime.Today;
    private int currentPage = 1;

    private int TotalPages => Math.Max(1, (reportResult.Orders.Count + OrdersPerPage - 1) / OrdersPerPage);

    private IEnumerable<MerchandiserPurchaseOrderReportOrderModel> PagedOrders => reportResult.Orders
        .Skip((currentPage - 1) * OrdersPerPage)
        .Take(OrdersPerPage);

    private int CurrentPageStartItem => reportResult.Orders.Count == 0
        ? 0
        : ((currentPage - 1) * OrdersPerPage) + 1;

    private int CurrentPageEndItem => Math.Min(currentPage * OrdersPerPage, reportResult.Orders.Count);

    private string OrderRegisterPageSummary => reportResult.Orders.Count == 0
        ? "0 total orders"
        : $"Page {currentPage} of {TotalPages} · {reportResult.Orders.Count} total orders";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadReportAsync();
        StateHasChanged();
    }

    private async Task LoadReportAsync()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            var selectedOrderId = selectedOrder?.SalesOrderId;
            var result = await Mediator.Send(new GetMerchandiserPurchaseOrderReportQuery(
                fromDate,
                toDate,
                ResolveSelectedMerchandiser(),
                ResolveAttachmentFilter(),
                string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim()));

            result.SwitchFirst(
                value =>
                {
                    reportResult = value;
                    selectedOrder = value.Orders.FirstOrDefault(order => order.SalesOrderId == selectedOrderId);
                },
                error =>
                {
                    reportResult = new GetMerchandiserPurchaseOrderReportResult();
                    selectedOrder = null;
                    errorMessage = error.Description;
                });

            EnsureValidCurrentPage();

            if (!result.IsError && !hasLoggedView)
            {
                hasLoggedView = true;
                await AuditService.LogAsync(AuditActions.ViewReports, "Report", "MerchandiserPurchaseOrderReport");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load merchandiser purchase order report page");
            reportResult = new GetMerchandiserPurchaseOrderReportResult();
            selectedOrder = null;
            errorMessage = "Failed to load merchandiser purchase order report.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task ApplyFiltersAsync()
    {
        currentPage = 1;
        await LoadReportAsync();
    }

    private async Task ClearFiltersAsync()
    {
        searchTerm = string.Empty;
        selectedMerchandiserValue = string.Empty;
        attachmentFilter = "all";
        fromDate = DateTime.Today.AddDays(-30);
        toDate = DateTime.Today;
        currentPage = 1;
        await LoadReportAsync();
    }

    private async Task ReloadAsync() => await LoadReportAsync();

    private void OnPageChanged(int page)
    {
        currentPage = page;
        StateHasChanged();
    }

    private async Task ExportToExcelAsync()
    {
        if (!reportResult.Orders.Any())
            return;

        isExporting = true;
        errorMessage = null;

        try
        {
            var bytes = ExportService.ExportMerchandiserPurchaseOrderReportToExcel(reportResult);
            var base64 = Convert.ToBase64String(bytes);
            await JS.InvokeVoidAsync(
                "downloadFile",
                $"Merchandiser_Purchase_Order_Report_{IAuditService.ToCAT(DateTime.UtcNow):yyyyMMdd_HHmm}.xlsx",
                base64);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export merchandiser purchase order report to Excel");
            errorMessage = "Failed to export the merchandiser purchase order report to Excel.";
        }
        finally
        {
            isExporting = false;
        }
    }

    private async Task ExportToPdfAsync()
    {
        if (!reportResult.Orders.Any())
            return;

        isExporting = true;
        errorMessage = null;

        try
        {
            await JS.InvokeVoidAsync("printReportHtml", BuildPdfHtml());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to export merchandiser purchase order report to PDF");
            errorMessage = "Failed to export the merchandiser purchase order report to PDF.";
        }
        finally
        {
            isExporting = false;
        }
    }

    private void OpenOrderDetails(MerchandiserPurchaseOrderReportOrderModel order) => selectedOrder = order;

    private async Task CloseSelectedOrderAsync()
    {
        selectedOrder = null;
        await CloseAttachmentViewerAsync();
    }

    private async Task DownloadAttachmentAsync(MerchandiserPurchaseOrderReportAttachmentModel attachment)
    {
        try
        {
            await JS.InvokeVoidAsync(
                "downloadAuthenticatedFile",
                $"/download/purchase-order/{attachment.AttachmentId}",
                attachment.FileName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to download purchase-order attachment {AttachmentId}", attachment.AttachmentId);
            errorMessage = "Failed to download the purchase-order attachment.";
        }
    }

    private static bool IsViewableAttachment(MerchandiserPurchaseOrderReportAttachmentModel attachment)
    {
        var mimeType = GetAttachmentViewerMimeType(attachment);
        return mimeType != null && (mimeType.StartsWith("image/") || mimeType == "application/pdf");
    }

    private static string? GetAttachmentViewerMimeType(MerchandiserPurchaseOrderReportAttachmentModel attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.MimeType))
            return attachment.MimeType;

        return Path.GetExtension(attachment.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            _ => null
        };
    }

    private async Task ViewAttachmentAsync(MerchandiserPurchaseOrderReportAttachmentModel attachment)
    {
        var mimeType = GetAttachmentViewerMimeType(attachment);

        if (mimeType == null)
            return;

        errorMessage = null;

        try
        {
            await RevokeAttachmentViewerDataUriAsync();

            attachmentViewerAttachment = attachment;
            attachmentViewerFileName = attachment.FileName;
            attachmentViewerMimeType = mimeType;
            attachmentViewerDataUri = null;
            showAttachmentViewer = true;
            isLoadingAttachmentViewer = true;
            StateHasChanged();

            attachmentViewerDataUri = await JS.InvokeAsync<string>(
                "createAuthenticatedObjectUrl",
                $"/download/purchase-order/{attachment.AttachmentId}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to preview purchase-order attachment {AttachmentId}", attachment.AttachmentId);
            errorMessage = "Failed to load the purchase-order attachment preview.";
            showAttachmentViewer = false;
        }
        finally
        {
            isLoadingAttachmentViewer = false;
            StateHasChanged();
        }
    }

    private async Task CloseAttachmentViewerAsync()
    {
        showAttachmentViewer = false;
        await RevokeAttachmentViewerDataUriAsync();
        attachmentViewerFileName = null;
        attachmentViewerMimeType = null;
        attachmentViewerAttachment = null;
    }

    private async Task RevokeAttachmentViewerDataUriAsync()
    {
        if (!string.IsNullOrWhiteSpace(attachmentViewerDataUri))
        {
            await JS.InvokeVoidAsync("revokeObjectUrl", attachmentViewerDataUri);
            attachmentViewerDataUri = null;
        }
    }

    private Guid? ResolveSelectedMerchandiser() =>
        Guid.TryParse(selectedMerchandiserValue, out var merchandiserUserId) ? merchandiserUserId : null;

    private void EnsureValidCurrentPage()
    {
        currentPage = Math.Clamp(currentPage, 1, TotalPages);
    }

    private bool? ResolveAttachmentFilter() => attachmentFilter switch
    {
        "with" => true,
        "without" => false,
        _ => null
    };

    private static string GetStatusCssClass(int status) => status switch
    {
        2 or 4 => "good",
        5 => "bad",
        6 => "hold",
        _ => "open"
    };

    private static string FormatMoney(string? currency, decimal amount) =>
        string.IsNullOrWhiteSpace(currency) ? amount.ToString("N2") : $"{currency} {amount:N2}";

    private static string FormatFileSize(long bytes)
    {
        const decimal kilobyte = 1024m;
        const decimal megabyte = kilobyte * 1024m;

        if (bytes >= megabyte)
            return $"{bytes / megabyte:N2} MB";
        if (bytes >= kilobyte)
            return $"{bytes / kilobyte:N1} KB";
        return $"{bytes:N0} bytes";
    }

    private static string FormatCatDate(DateTime utcDateTime) =>
        IAuditService.ToCAT(EnsureUtc(utcDateTime)).ToString("dd MMM yyyy");

    private static string FormatCatDateTime(DateTime? utcDateTime)
    {
        if (!utcDateTime.HasValue || utcDateTime.Value == default)
            return "Not available";

        return $"{IAuditService.ToCAT(EnsureUtc(utcDateTime.Value)):dd MMM yyyy HH:mm} CAT";
    }

    private string BuildPdfHtml()
    {
        var builder = new StringBuilder();

        builder.Append("<div class='kpi-row'>");
        AppendKpi(builder, reportResult.TotalMerchandisers.ToString("N0"), "Merchandisers");
        AppendKpi(builder, reportResult.TotalOrders.ToString("N0"), "Orders");
        AppendKpi(builder, reportResult.OrdersWithAttachments.ToString("N0"), "With PO", "success");
        AppendKpi(builder, reportResult.OrdersWithoutAttachments.ToString("N0"), "Without PO", "warning");
        AppendKpi(builder, reportResult.TotalAttachments.ToString("N0"), "Attachments");
        AppendKpi(builder, reportResult.TotalOrderValue.ToString("N2"), "Total Value");
        builder.Append("</div>");

        builder.Append("<h3 style='font-size:14px;margin-top:20px;'>Merchandiser Breakdown</h3>");
        builder.Append("<table><thead><tr><th>Merchandiser</th><th>Orders</th><th>With PO</th><th>Attachments</th><th>Synced</th><th>Total Value</th><th>Latest Activity</th></tr></thead><tbody>");
        foreach (var merchandiser in reportResult.Merchandisers)
        {
            builder.Append($"<tr><td><strong>{Html(merchandiser.FullName)}</strong><br/><small>{Html(merchandiser.Username)}</small></td><td class='text-end'>{merchandiser.OrderCount:N0}</td><td class='text-end'>{merchandiser.OrdersWithAttachments:N0}</td><td class='text-end'>{merchandiser.AttachmentCount:N0}</td><td class='text-end'>{merchandiser.SyncedOrders:N0}</td><td class='text-end'>{merchandiser.TotalOrderValue:N2}</td><td>{Html(FormatCatDateTime(merchandiser.LatestOrderCreatedAtUtc))}</td></tr>");
        }
        builder.Append("</tbody></table>");

        builder.Append("<h3 style='font-size:14px;margin-top:20px;'>Order Register</h3>");
        builder.Append("<table><thead><tr><th>Order</th><th>Merchandiser</th><th>Customer</th><th>SAP</th><th>Created</th><th>Total</th><th>PO Files</th></tr></thead><tbody>");
        foreach (var order in reportResult.Orders)
        {
            builder.Append($"<tr><td><strong>{Html(order.OrderNumber)}</strong><br/><small>{Html(order.AttachmentReference)}</small><br/><small>{Html(order.StatusLabel)}</small></td><td><strong>{Html(order.MerchandiserFullName)}</strong><br/><small>{Html(order.MerchandiserUsername)}</small></td><td><strong>{Html(order.CardCode)}</strong><br/><small>{Html(order.CardName)}</small></td><td><strong>{Html(order.SapDocNum?.ToString() ?? "Pending")}</strong><br/><small>DocEntry {Html(order.SapDocEntry?.ToString() ?? "Not synced")}</small></td><td>{Html(FormatCatDateTime(order.CreatedAtUtc))}<br/><small>Order {Html(FormatCatDate(order.OrderDateUtc))}</small></td><td class='text-end'>{Html(FormatMoney(order.Currency, order.DocTotal))}<br/><small>{order.ItemCount:N0} lines</small></td><td class='text-end'>{order.AttachmentCount:N0}</td></tr>");
        }
        builder.Append("</tbody></table>");

        foreach (var order in reportResult.Orders)
        {
            builder.Append($"<div style='page-break-inside:avoid;margin-top:24px;'><h3 style='font-size:14px;margin-bottom:8px;'>Order {Html(order.OrderNumber)} · {Html(order.MerchandiserFullName)}</h3>");
            builder.Append("<table><thead><tr><th>Field</th><th>Value</th><th>Field</th><th>Value</th></tr></thead><tbody>");
            builder.Append($"<tr><th>Attachment Ref</th><td>{Html(order.AttachmentReference)}</td><th>Status</th><td>{Html(order.StatusLabel)}</td></tr>");
            builder.Append($"<tr><th>Customer</th><td>{Html($"{order.CardCode} - {order.CardName}")}</td><th>Created</th><td>{Html(FormatCatDateTime(order.CreatedAtUtc))}</td></tr>");
            builder.Append($"<tr><th>SAP Doc #</th><td>{Html(order.SapDocNum?.ToString() ?? "Pending")}</td><th>SAP DocEntry</th><td>{Html(order.SapDocEntry?.ToString() ?? "Not synced")}</td></tr>");
            builder.Append($"<tr><th>Total</th><td>{Html(FormatMoney(order.Currency, order.DocTotal))}</td><th>Quantity</th><td>{order.TotalQuantity:N2}</td></tr>");
            builder.Append($"<tr><th>Notes</th><td colspan='3'>{Html(string.IsNullOrWhiteSpace(order.MerchandiserNotes) ? "No merchandiser notes" : order.MerchandiserNotes)}</td></tr>");
            builder.Append("</tbody></table>");

            builder.Append("<h3 style='font-size:13px;margin-top:18px;'>Uploaded Purchase Orders</h3>");
            if (order.Attachments.Any())
            {
                builder.Append("<table><thead><tr><th>File Name</th><th>MIME</th><th>Size</th><th>Uploaded</th><th>Uploaded By</th><th>Description</th></tr></thead><tbody>");
                foreach (var attachment in order.Attachments)
                {
                    builder.Append($"<tr><td>{Html(attachment.FileName)}</td><td>{Html(attachment.MimeType)}</td><td class='text-end'>{attachment.FileSizeBytes:N0}</td><td>{Html(FormatCatDateTime(attachment.UploadedAtUtc))}</td><td>{Html(attachment.UploadedByUsername)}</td><td>{Html(attachment.Description)}</td></tr>");
                }
                builder.Append("</tbody></table>");
            }
            else
            {
                builder.Append("<p style='color:#616161;font-style:italic;'>No uploaded purchase-order attachments for this order.</p>");
            }

            builder.Append("<h3 style='font-size:13px;margin-top:18px;'>Order Lines</h3>");
            if (order.Lines.Any())
            {
                builder.Append("<table><thead><tr><th>Line</th><th>Item Code</th><th>Description</th><th>Qty</th><th>Fulfilled</th><th>Warehouse</th><th>Line Total</th></tr></thead><tbody>");
                foreach (var line in order.Lines)
                {
                    builder.Append($"<tr><td class='text-end'>{line.LineNum}</td><td>{Html(line.ItemCode)}</td><td>{Html(line.ItemDescription)}</td><td class='text-end'>{line.Quantity:N2}</td><td class='text-end'>{line.QuantityFulfilled:N2}</td><td>{Html(line.WarehouseCode)}</td><td class='text-end'>{Html(FormatMoney(order.Currency, line.LineTotal))}</td></tr>");
                }
                builder.Append("</tbody></table>");
            }
            else
            {
                builder.Append("<p style='color:#616161;font-style:italic;'>No line items were returned for this order.</p>");
            }

            builder.Append("</div>");
        }

        return ExportService.GeneratePrintableHtml(
            "Merchandiser Purchase Order Report",
            builder.ToString(),
            reportResult.FromDate ?? fromDate,
            reportResult.ToDate ?? toDate);
    }

    private static void AppendKpi(StringBuilder builder, string value, string label, string? cssClass = null)
    {
        var classSuffix = string.IsNullOrWhiteSpace(cssClass) ? string.Empty : $" {cssClass}";
        builder.Append($"<div class='kpi{classSuffix}'><h3>{Html(value)}</h3><p>{Html(label)}</p></div>");
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RevokeAttachmentViewerDataUriAsync();
        }
        catch
        {
        }
    }
}