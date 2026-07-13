using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;

public sealed class GetInvoiceByDocNumHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IRevmaxClient revmaxClient,
    ISender sender,
    IAuditService auditService,
    IDocumentService documentService,
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

        Invoice? invoice;
        try
        {
            invoice = await sapClient.GetInvoiceByDocNumAsync(request.DocNum, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            var cachedResult = await TryGetCachedInvoiceResultAsync(request, "SAP timeout", cancellationToken);
            if (cachedResult.HasValue)
                return cachedResult.Value;

            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            var cachedResult = await TryGetCachedInvoiceResultAsync(request, "SAP connection error", cancellationToken);
            if (cachedResult.HasValue)
                return cachedResult.Value;

            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving invoice by DocNum {DocNum}", request.DocNum);

            // Some SAP invoices contain line-level values that cannot be deserialized into the
            // full Invoice model. Retry with the existing header-only bulk lookup before treating
            // a document that exists in OINV as unavailable to the POD workflow.
            var headerInvoice = await TryGetSapInvoiceHeaderAsync(request.DocNum, cancellationToken);
            if (headerInvoice is not null)
                return await AuthorizeAndEnrichInvoiceAsync(headerInvoice.ToDto(), request, cancellationToken);

            var cachedResult = await TryGetCachedInvoiceResultAsync(request, "SAP lookup error", cancellationToken);
            if (cachedResult.HasValue)
                return cachedResult.Value;

            return Errors.Invoice.CreationFailed(ex.Message);
        }

        if (invoice is null)
        {
            // Repeat the exact lookup with a small $select projection. Besides reducing payload
            // size, this avoids line-level conversion problems while still proving the SAP
            // document exists and supplying everything required by POD upload.
            invoice = await TryGetSapInvoiceHeaderAsync(request.DocNum, cancellationToken);
            if (invoice is null)
                return Errors.Invoice.NotFoundByDocNum(request.DocNum);
        }

        return await AuthorizeAndEnrichInvoiceAsync(invoice.ToDto(), request, cancellationToken);
    }

    private async Task<Invoice?> TryGetSapInvoiceHeaderAsync(
        int docNum,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoices = await sapClient.GetInvoicesByDocNumsAsync([docNum], cancellationToken);
            var invoice = invoices.FirstOrDefault(candidate => candidate.DocNum == docNum);

            if (invoice is not null)
            {
                logger.LogWarning(
                    "Resolved invoice {DocNum} through the SAP header-only fallback after the full lookup did not return a usable invoice",
                    docNum);
            }

            return invoice;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAP header-only fallback failed for invoice {DocNum}", docNum);
            return null;
        }
    }

    private async Task<ErrorOr<InvoiceDto>?> TryGetCachedInvoiceResultAsync(
        GetInvoiceByDocNumQuery request,
        string reason,
        CancellationToken cancellationToken)
    {
        var cachedInvoice = await db.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.DocumentLines)
            .Where(invoice =>
                invoice.SAPDocNum == request.DocNum &&
                invoice.SAPDocEntry.HasValue &&
                invoice.SAPDocEntry.Value > 0)
            .OrderByDescending(invoice => invoice.DocumentLines.Any())
            .ThenByDescending(invoice => !string.IsNullOrWhiteSpace(invoice.CardCode))
            .ThenByDescending(invoice => !string.IsNullOrWhiteSpace(invoice.CardName))
            .ThenByDescending(invoice => invoice.UpdatedAt ?? invoice.CreatedAt)
            .ThenByDescending(invoice => invoice.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (cachedInvoice is null)
        {
            return null;
        }

        logger.LogWarning(
            "Serving invoice {DocNum} from local cache because live SAP lookup failed: {Reason}",
            request.DocNum,
            reason);

        return await AuthorizeAndEnrichInvoiceAsync(MapCachedInvoiceToDto(cachedInvoice), request, cancellationToken);
    }

    private async Task<ErrorOr<InvoiceDto>> AuthorizeAndEnrichInvoiceAsync(
        InvoiceDto invoiceDto,
        GetInvoiceByDocNumQuery request,
        CancellationToken cancellationToken)
    {
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
                    .Any(code => string.Equals(code, invoiceDto.CardCode, StringComparison.OrdinalIgnoreCase));
            }
            else if (isScopedPodViewer)
            {
                if (string.IsNullOrWhiteSpace(user.AssignedSection) || invoiceDto.DocEntry <= 0)
                {
                    canAccessInvoice = false;
                }
                else
                {
                    var scopedDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync(
                        [invoiceDto.DocEntry],
                        user.AssignedSection,
                        cancellationToken);

                    canAccessInvoice = scopedDocEntries.Contains(invoiceDto.DocEntry);
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

    private static InvoiceDto MapCachedInvoiceToDto(InvoiceEntity invoice)
    {
        return new InvoiceDto
        {
            DocEntry = invoice.SAPDocEntry!.Value,
            DocNum = invoice.SAPDocNum!.Value,
            DocDate = invoice.DocDate.ToString("yyyy-MM-dd"),
            DocDueDate = invoice.DocDueDate?.ToString("yyyy-MM-dd"),
            CardCode = invoice.CardCode,
            CardName = invoice.CardName,
            NumAtCard = invoice.NumAtCard,
            Comments = invoice.Comments,
            DocStatus = invoice.Status,
            DocTotal = invoice.DocTotal,
            VatSum = invoice.VatSum,
            DocCurrency = invoice.DocCurrency,
            Lines = invoice.DocumentLines
                .OrderBy(line => line.LineNum)
                .Select(line => new InvoiceLineDto
                {
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    GrossPrice = line.Price,
                    LineTotal = line.LineTotal,
                    TaxCode = line.TaxCode,
                    WarehouseCode = line.WarehouseCode,
                    DiscountPercent = line.DiscountPercent,
                    UoMCode = line.UoMCode
                })
                .ToList()
        };
    }
}
