using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Pods;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;

public sealed class GetInvoiceByDocNumHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IRevmaxClient revmaxClient,
    ISender sender,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    IOptions<RevmaxSettings> revmaxSettings,
    ILogger<GetInvoiceByDocNumHandler> logger
) : IRequestHandler<GetInvoiceByDocNumQuery, ErrorOr<InvoiceDto>>
{
    public async Task<ErrorOr<InvoiceDto>> Handle(
        GetInvoiceByDocNumQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        try
        {
            var invoice = await sapClient.GetInvoiceByDocNumAsync(request.DocNum, cancellationToken);
            if (invoice is null)
                return Errors.Invoice.NotFoundByDocNum(request.DocNum);

            if (request.RestrictToAssignedCustomers)
            {
                if (!request.RequestingUserId.HasValue)
                    return Errors.Auth.Unauthenticated;

                var user = await db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == request.RequestingUserId.Value, cancellationToken);

                if (user is null)
                    return Errors.Auth.UserNotFound;

                var isDriver = string.Equals(user.Role, "Driver", StringComparison.OrdinalIgnoreCase);
                var isScopedPodViewer = string.Equals(user.Role, "PodOperator", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(user.Role, "Operator", StringComparison.OrdinalIgnoreCase);

                var canAccessInvoice = true;

                if (isDriver)
                {
                    var customerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
                        db,
                        user,
                        logger,
                        cancellationToken);

                    canAccessInvoice = customerCodes
                        .Any(code => string.Equals(code, invoice.CardCode, StringComparison.OrdinalIgnoreCase));
                }
                else if (isScopedPodViewer)
                {
                    if (string.IsNullOrWhiteSpace(user.AssignedSection))
                    {
                        canAccessInvoice = false;
                    }
                    else
                    {
                        var warehouseLocations = PodLocationScope.BuildWarehouseLocationLookup(
                            await sapClient.GetWarehousesAsync(cancellationToken));

                        canAccessInvoice = PodLocationScope.InvoiceMatchesAssignedSection(
                            invoice,
                            user.AssignedSection,
                            warehouseLocations);
                    }
                }

                if (!canAccessInvoice)
                {
                    try
                    {
                        await auditService.LogAsync(
                            AuditActions.ViewInvoices,
                            "Invoice",
                            request.DocNum.ToString(),
                            $"Blocked invoice lookup for unassigned invoice #{request.DocNum}.",
                            false,
                            "Invoice customer is not assigned to the current mobile user.");
                    }
                    catch
                    {
                    }

                    return Errors.Invoice.NotFoundByDocNum(request.DocNum);
                }
            }

            var invoiceDto = invoice.ToDto();
            await FiscalDocumentStatusProjector.EnrichInvoiceAsync(db, invoiceDto, cancellationToken);

            if (revmaxSettings.Value.Enabled
                && string.Equals(invoiceDto.FiscalizationStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                await InvoiceFiscalTransactionSync.SyncAsync(
                    invoiceDto,
                    revmaxClient,
                    sender,
                    logger,
                    cancellationToken);
            }

            return invoiceDto;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving invoice by DocNum {DocNum}", request.DocNum);
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
