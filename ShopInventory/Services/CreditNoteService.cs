using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using System.Text.Json;

namespace ShopInventory.Services;

/// <summary>
/// Service implementation for Credit Note operations - Fetches from SAP Business One
/// </summary>
public class CreditNoteService : ICreditNoteService
{
    private const decimal CreditAmountTolerance = 0.01m;

    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly IFiscalizationService _fiscalizationService;
    private readonly ICreditNoteProjectionSyncService _projectionSyncService;
    private readonly ILogger<CreditNoteService> _logger;

    public CreditNoteService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        IFiscalizationService fiscalizationService,
        ICreditNoteProjectionSyncService projectionSyncService,
        ILogger<CreditNoteService> logger)
    {
        _context = context;
        _sapClient = sapClient;
        _fiscalizationService = fiscalizationService;
        _projectionSyncService = projectionSyncService;
        _logger = logger;
    }

    public async Task<CreditNoteDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        // Try to get from SAP first (by DocEntry)
        try
        {
            var sapCreditNote = await _sapClient.GetCreditNoteByDocEntryAsync(id, cancellationToken);
            if (sapCreditNote != null)
                return MapFromSAP(sapCreditNote);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch credit note {Id} from SAP, falling back to local DB", id);
        }

        // Fallback to local database
        var creditNote = await _context.CreditNotes
            .Include(c => c.Lines)
            .Include(c => c.CreatedByUser)
            .Include(c => c.ApprovedByUser)
            .Include(c => c.OriginalInvoice)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id || c.SAPDocEntry == id, cancellationToken);

        return creditNote == null ? null : MapToDto(creditNote);
    }

    public async Task<CreditNoteDto?> GetByCreditNoteNumberAsync(string creditNoteNumber, CancellationToken cancellationToken = default)
    {
        var creditNote = await _context.CreditNotes
            .Include(c => c.Lines)
            .Include(c => c.CreatedByUser)
            .Include(c => c.ApprovedByUser)
            .Include(c => c.OriginalInvoice)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CreditNoteNumber == creditNoteNumber, cancellationToken);

        return creditNote == null ? null : MapToDto(creditNote);
    }

    public async Task<List<CreditNoteDto>> GetByInvoiceIdAsync(int invoiceId, CancellationToken cancellationToken = default)
    {
        // Try to get from SAP first
        try
        {
            var sapCreditNotes = await _sapClient.GetCreditNotesByInvoiceAsync(invoiceId, cancellationToken);
            if (sapCreditNotes.Any())
                return sapCreditNotes.Select(MapFromSAP).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch credit notes for invoice {InvoiceId} from SAP", invoiceId);
        }

        // Fallback to local database
        var creditNotes = await _context.CreditNotes
            .Include(c => c.Lines)
            .Include(c => c.CreatedByUser)
            .Include(c => c.ApprovedByUser)
            .Where(c => c.OriginalInvoiceId == invoiceId || c.OriginalInvoiceDocEntry == invoiceId)
            .AsNoTracking()
            .OrderByDescending(c => c.CreditNoteDate)
            .ToListAsync(cancellationToken);

        return creditNotes.Select(MapToDto).ToList();
    }

    public async Task<CreditNoteListResponseDto> GetAllAsync(int page, int pageSize, CreditNoteStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, bool includeLines = false,
        CancellationToken cancellationToken = default)
    {
        // The local projection answers this list in one Postgres query. SAP is the fallback, for
        // when the projection is switched off, still backfilling, or stale — and for a caller that
        // needs the lines, because the line snapshot carries no quantity to aggregate.
        if (!includeLines && await IsProjectionReadableAsync(cancellationToken))
        {
            try
            {
                return await GetAllFromProjectionAsync(page, pageSize, status, cardCode, fromDate, toDate, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read the credit-note projection, falling back to SAP");
            }
        }

        try
        {
            // Fetch from SAP
            _logger.LogInformation("Fetching credit notes from SAP - Page: {Page}, PageSize: {PageSize}", page, pageSize);

            List<SAPCreditNote> sapCreditNotes;
            int totalCount;

            if (!string.IsNullOrEmpty(cardCode) && fromDate.HasValue && toDate.HasValue)
            {
                sapCreditNotes = await _sapClient.GetCreditNotesByCustomerAsync(cardCode, fromDate.Value, toDate.Value, cancellationToken);
                totalCount = sapCreditNotes.Count;
            }
            else if (!string.IsNullOrEmpty(cardCode))
            {
                sapCreditNotes = await _sapClient.GetCreditNotesByCustomerAsync(cardCode, cancellationToken);
                totalCount = sapCreditNotes.Count;
            }
            else if (fromDate.HasValue && toDate.HasValue)
            {
                sapCreditNotes = await _sapClient.GetCreditNotesByDateRangeAsync(fromDate.Value, toDate.Value, cancellationToken);
                totalCount = sapCreditNotes.Count;
            }
            else
            {
                // No filters at all - use date range default to avoid fetching entire SAP dataset
                var todayUtc = DateTime.UtcNow.Date;
                var defaultFrom = new DateTime(todayUtc.Year, todayUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var defaultTo = todayUtc;
                sapCreditNotes = await _sapClient.GetCreditNotesByDateRangeAsync(defaultFrom, defaultTo, cancellationToken);
                totalCount = sapCreditNotes.Count;
            }

            return new CreditNoteListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                CreditNotes = sapCreditNotes.Select(MapFromSAP).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch credit notes from SAP, falling back to local DB");
            return await GetAllFromLocalAsync(page, pageSize, status, cardCode, fromDate, toDate, cancellationToken);
        }
    }

    private async Task<bool> IsProjectionReadableAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _projectionSyncService.IsReadyForReadsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check whether the credit-note projection is readable");
            return false;
        }
    }

    /// <summary>
    /// Serves the credit-note list from the local SAP projection.
    /// </summary>
    /// <remarks>
    /// Headers only: the projection keeps no item description, quantity or unit price, so the lines
    /// a detail view needs are read per document by <see cref="GetByIdAsync"/> rather than carried
    /// on every row of a list that never shows them. That is also why this is fast — the SAP read it
    /// replaces pulled every document's nested DocumentLines to render a table of headers.
    /// </remarks>
    private async Task<CreditNoteListResponseDto> GetAllFromProjectionAsync(int page, int pageSize, CreditNoteStatus? status,
        string? cardCode, DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken)
    {
        var (rangeFrom, rangeTo) = ResolveProjectionDateRange(cardCode, fromDate, toDate);

        var query = _context.SapCreditNoteSnapshots.AsNoTracking();

        if (!string.IsNullOrEmpty(cardCode))
            query = query.Where(snapshot => snapshot.CardCode == cardCode);

        if (rangeFrom.HasValue)
            query = query.Where(snapshot => snapshot.DocDate >= rangeFrom.Value);

        if (rangeTo.HasValue)
            query = query.Where(snapshot => snapshot.DocDate <= rangeTo.Value);

        var snapshots = await query
            .OrderByDescending(snapshot => snapshot.DocDate)
            .ThenByDescending(snapshot => snapshot.SapDocEntry)
            .Select(snapshot => new ProjectedCreditNote(
                snapshot.SapDocEntry,
                snapshot.SapDocNum,
                snapshot.DocDate,
                snapshot.CardCode,
                snapshot.CardName,
                snapshot.DocCurrency,
                snapshot.Comments,
                snapshot.DocTotal,
                snapshot.VatSum,
                snapshot.DocumentStatus,
                snapshot.IsCancelled))
            .ToListAsync(cancellationToken);

        // Status is derived from DocumentStatus and IsCancelled rather than stored, so it is applied
        // after mapping — before paging, so the count and the page agree.
        var creditNotes = snapshots
            .Select(MapFromProjection)
            .Where(note => !status.HasValue || note.Status == status.Value)
            .ToList();

        var safePageSize = Math.Max(1, pageSize);
        var totalCount = creditNotes.Count;

        _logger.LogInformation(
            "Served {Count} credit notes from the local projection between {From:yyyy-MM-dd} and {To:yyyy-MM-dd}",
            totalCount,
            rangeFrom,
            rangeTo);

        return new CreditNoteListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)safePageSize),
            CreditNotes = creditNotes
                .Skip(Math.Max(0, page - 1) * safePageSize)
                .Take(safePageSize)
                .ToList()
        };
    }

    /// <summary>
    /// Mirrors the SAP path's bounds: an unfiltered request defaults to the current month rather
    /// than the whole history, and a customer lookup with no dates is left unbounded.
    /// </summary>
    private static (DateTime? From, DateTime? To) ResolveProjectionDateRange(
        string? cardCode, DateTime? fromDate, DateTime? toDate)
    {
        // DocDate is a date column written as a UTC-kind midnight, so both bounds are inclusive.
        if (fromDate.HasValue || toDate.HasValue)
        {
            return (ToProjectionDate(fromDate), ToProjectionDate(toDate));
        }

        if (!string.IsNullOrEmpty(cardCode))
        {
            return (null, null);
        }

        var todayUtc = DateTime.UtcNow.Date;
        return (
            ToProjectionDate(new DateTime(todayUtc.Year, todayUtc.Month, 1)),
            ToProjectionDate(todayUtc));
    }

    private static DateTime? ToProjectionDate(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc) : null;

    private static CreditNoteDto MapFromProjection(ProjectedCreditNote snapshot) => new()
    {
        Id = snapshot.SapDocEntry,
        SAPDocEntry = snapshot.SapDocEntry,
        SAPDocNum = snapshot.SapDocNum,
        CreditNoteNumber = $"SAP-CN-{snapshot.SapDocNum}",
        CreditNoteDate = snapshot.DocDate,
        CardCode = snapshot.CardCode ?? string.Empty,
        CardName = snapshot.CardName,
        Type = CreditNoteType.Return, // Default type for SAP credit notes
        Status = MapSAPStatusToLocal(snapshot.DocumentStatus, snapshot.IsCancelled ? "tYES" : null),
        Reason = snapshot.Comments,
        Comments = snapshot.Comments,
        Currency = snapshot.DocCurrency,
        ExchangeRate = 1,
        SubTotal = snapshot.DocTotal - snapshot.VatSum,
        TaxAmount = snapshot.VatSum,
        DocTotal = snapshot.DocTotal,
        AppliedAmount = 0,
        Balance = snapshot.DocTotal,
        IsSynced = true,
        Lines = new List<CreditNoteLineDto>()
    };

    /// <summary>The header columns the list needs, so EF reads no lines and no sync bookkeeping.</summary>
    private sealed record ProjectedCreditNote(
        int SapDocEntry,
        int SapDocNum,
        DateTime DocDate,
        string? CardCode,
        string? CardName,
        string? DocCurrency,
        string? Comments,
        decimal DocTotal,
        decimal VatSum,
        string? DocumentStatus,
        bool IsCancelled);

    private async Task<CreditNoteListResponseDto> GetAllFromLocalAsync(int page, int pageSize, CreditNoteStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        var fromDateUtc = NormalizeUtcDateStart(fromDate);
        var toDateExclusiveUtc = NormalizeUtcDateExclusiveEnd(toDate);

        var query = _context.CreditNotes
            .AsNoTracking()
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(c => c.Status == status.Value);

        if (!string.IsNullOrEmpty(cardCode))
            query = query.Where(c => c.CardCode == cardCode);

        if (fromDateUtc.HasValue)
            query = query.Where(c => c.CreditNoteDate >= fromDateUtc.Value);

        if (toDateExclusiveUtc.HasValue)
            query = query.Where(c => c.CreditNoteDate < toDateExclusiveUtc.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var creditNotes = await query
            .OrderByDescending(c => c.CreditNoteDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CreditNoteDto
            {
                Id = c.Id,
                SAPDocEntry = c.SAPDocEntry,
                SAPDocNum = c.SAPDocNum,
                CreditNoteNumber = c.CreditNoteNumber,
                CreditNoteDate = c.CreditNoteDate,
                CardCode = c.CardCode,
                CardName = c.CardName,
                Type = c.Type,
                Status = c.Status,
                OriginalInvoiceId = c.OriginalInvoiceId,
                OriginalInvoiceDocEntry = c.OriginalInvoiceDocEntry,
                Reason = c.Reason,
                Comments = c.Comments,
                Currency = c.Currency,
                ExchangeRate = c.ExchangeRate,
                SubTotal = c.SubTotal,
                TaxAmount = c.TaxAmount,
                DocTotal = c.DocTotal,
                AppliedAmount = c.AppliedAmount,
                Balance = c.Balance,
                RestockItems = c.RestockItems,
                RestockWarehouseCode = c.RestockWarehouseCode,
                CreatedByUserId = c.CreatedByUserId,
                CreatedByUserName = c.CreatedByUser != null ? c.CreatedByUser.Username : null,
                ApprovedByUserId = c.ApprovedByUserId,
                ApprovedByUserName = c.ApprovedByUser != null ? c.ApprovedByUser.Username : null,
                ApprovedDate = c.ApprovedDate,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt,
                IsSynced = c.IsSynced,
                Lines = c.Lines.Select(l => new CreditNoteLineDto
                {
                    Id = l.Id,
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    DiscountPercent = l.DiscountPercent,
                    TaxPercent = l.TaxPercent,
                    LineTotal = l.LineTotal,
                    WarehouseCode = l.WarehouseCode,
                    ReturnReason = l.ReturnReason,
                    BatchNumber = l.BatchNumber,
                    IsRestocked = l.IsRestocked
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        return new CreditNoteListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            CreditNotes = creditNotes
        };
    }

    public async Task<CreditNoteDto> CreateAsync(CreateCreditNoteRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var creditNoteNumber = await GenerateCreditNoteNumberAsync(cancellationToken);

        var creditNote = new CreditNoteEntity
        {
            CreditNoteNumber = creditNoteNumber,
            CreditNoteDate = DateTime.UtcNow,
            CardCode = request.CardCode,
            CardName = request.CardName,
            Type = request.Type,
            Status = CreditNoteStatus.Draft,
            OriginalInvoiceId = request.OriginalInvoiceId,
            OriginalInvoiceDocEntry = request.OriginalInvoiceDocEntry,
            Reason = request.Reason,
            Comments = request.Comments,
            Currency = request.Currency ?? "USD",
            RestockItems = request.RestockItems,
            RestockWarehouseCode = request.RestockWarehouseCode,
            CreatedByUserId = userId
        };

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            var line = new CreditNoteLineEntity
            {
                LineNum = lineNum++,
                ItemCode = lineRequest.ItemCode,
                ItemDescription = lineRequest.ItemDescription,
                Quantity = lineRequest.Quantity,
                UnitPrice = lineRequest.UnitPrice,
                DiscountPercent = lineRequest.DiscountPercent,
                TaxPercent = lineRequest.TaxPercent,
                LineTotal = lineTotal,
                WarehouseCode = lineRequest.WarehouseCode ?? request.RestockWarehouseCode,
                ReturnReason = lineRequest.ReturnReason,
                BatchNumber = lineRequest.BatchNumber,
                OriginalInvoiceLineId = lineRequest.OriginalInvoiceLineId
            };

            creditNote.Lines.Add(line);
            subTotal += lineTotal;
            taxAmount += lineTax;
        }

        creditNote.SubTotal = subTotal;
        creditNote.TaxAmount = taxAmount;
        creditNote.DocTotal = subTotal + taxAmount;
        creditNote.Balance = creditNote.DocTotal;

        // Post to SAP Business One first
        SAPCreditNote sapCreditNote;
        try
        {
            _logger.LogInformation("Posting credit note to SAP for customer {CardCode}", request.CardCode);
            sapCreditNote = await _sapClient.CreateCreditNoteAsync(request, cancellationToken);

            // Update local entity with SAP reference
            creditNote.SAPDocEntry = sapCreditNote.DocEntry;
            creditNote.SAPDocNum = sapCreditNote.DocNum;
            creditNote.Status = CreditNoteStatus.Approved; // Set to approved since it's now in SAP

            _logger.LogInformation("Credit note posted to SAP successfully. DocEntry: {DocEntry}, DocNum: {DocNum}",
                sapCreditNote.DocEntry, sapCreditNote.DocNum);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post credit note to SAP. Credit note will NOT be saved locally.");
            throw new InvalidOperationException($"Failed to post credit note to SAP: {ex.Message}", ex);
        }

        // FISCALISE after successful SAP posting
        FiscalizationResult? fiscalizationResult = null;
        try
        {
            // For credit notes, we need the original invoice number
            var originalInvoiceNumber = request.OriginalInvoiceDocEntry?.ToString() ?? "";

            if (!string.IsNullOrEmpty(originalInvoiceNumber))
            {
                var creditNoteDto = new InvoiceDto
                {
                    DocEntry = sapCreditNote.DocEntry,
                    DocNum = sapCreditNote.DocNum,
                    CardCode = sapCreditNote.CardCode,
                    CardName = sapCreditNote.CardName,
                    DocTotal = Math.Abs(sapCreditNote.DocTotal),
                    VatSum = Math.Abs(sapCreditNote.VatSum),
                    DocCurrency = sapCreditNote.DocCurrency,
                    Comments = request.Reason,
                    Lines = sapCreditNote.DocumentLines?.Select(l => new InvoiceLineDto
                    {
                        LineNum = l.LineNum,
                        ItemCode = l.ItemCode,
                        ItemDescription = l.ItemDescription,
                        Quantity = Math.Abs(l.Quantity),
                        UnitPrice = l.UnitPrice,
                        LineTotal = Math.Abs(l.LineTotal),
                        TaxCode = l.TaxCode,
                        WarehouseCode = l.WarehouseCode
                    }).ToList()
                };

                fiscalizationResult = await _fiscalizationService.FiscalizeCreditNoteAsync(
                    creditNoteDto,
                    originalInvoiceNumber,
                    new CustomerFiscalDetails { CustomerName = sapCreditNote.CardName },
                    cancellationToken);

                if (fiscalizationResult.Success)
                {
                    _logger.LogInformation(
                        "Credit note {DocNum} fiscalized successfully. QRCode: {HasQR}, ReceiptGlobalNo: {ReceiptNo}",
                        sapCreditNote.DocNum,
                        !string.IsNullOrEmpty(fiscalizationResult.QRCode),
                        fiscalizationResult.ReceiptGlobalNo);
                }
                else
                {
                    _logger.LogWarning(
                        "Credit note {DocNum} fiscalization failed: {Message}. Credit note was created in SAP.",
                        sapCreditNote.DocNum, fiscalizationResult.Message);

                    await CaptureCreditNoteFiscalizationIncidentAsync(
                        creditNote.CreditNoteNumber,
                        sapCreditNote.DocNum,
                        request.CardCode,
                        fiscalizationResult.Message ?? "Fiscalisation failed for the credit note.",
                        cancellationToken);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Cannot fiscalize credit note {DocNum}: No original invoice reference",
                    sapCreditNote.DocNum);

                await CaptureCreditNoteFiscalizationIncidentAsync(
                    creditNote.CreditNoteNumber,
                    sapCreditNote.DocNum,
                    request.CardCode,
                    "Fiscalisation skipped because the original invoice reference was missing.",
                    cancellationToken);
            }
        }
        catch (Exception fiscalEx)
        {
            _logger.LogError(fiscalEx,
                "Error during fiscalization of credit note {DocNum}. Credit note was created in SAP.",
                sapCreditNote.DocNum);

            await CaptureCreditNoteFiscalizationIncidentAsync(
                creditNote.CreditNoteNumber,
                sapCreditNote.DocNum,
                request.CardCode,
                fiscalEx.Message,
                cancellationToken);
        }

        // Save to local database only after successful SAP posting
        _context.CreditNotes.Add(creditNote);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            await _projectionSyncService.UpsertAsync([sapCreditNote], cancellationToken);
        }
        catch (Exception projectionException)
        {
            // SAP and the operational credit-note record are authoritative. Projection repair is
            // best-effort here and the clustered sync job will reconcile it shortly.
            _logger.LogWarning(
                projectionException,
                "Failed to write through SAP credit note {DocEntry} to the local projection",
                sapCreditNote.DocEntry);
        }

        _logger.LogInformation("Created credit note {CreditNoteNumber} for customer {CardCode} with SAP DocEntry {DocEntry}",
            creditNoteNumber, request.CardCode, creditNote.SAPDocEntry);

        return MapToDto(creditNote);
    }

    public async Task<CreditNoteDto> CreateFromInvoiceAsync(int invoiceId, List<CreateCreditNoteLineRequest> lines,
        string reason, Guid userId, CancellationToken cancellationToken = default)
    {
        // Always fetch from SAP to get batch numbers for batch-managed items
        _logger.LogInformation("Fetching invoice {InvoiceId} from SAP for credit note creation", invoiceId);

        Invoice? sapInvoice;
        try
        {
            sapInvoice = await _sapClient.GetInvoiceByDocEntryAsync(invoiceId, cancellationToken);

            if (sapInvoice == null)
            {
                _logger.LogWarning("Invoice {InvoiceId} not found in SAP", invoiceId);
                throw new InvalidOperationException($"Invoice with DocEntry {invoiceId} not found in SAP Business One");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Error fetching invoice {InvoiceId} from SAP", invoiceId);
            throw new InvalidOperationException($"Failed to fetch invoice {invoiceId} from SAP: {ex.Message}");
        }

        _logger.LogInformation("Found invoice {InvoiceId} in SAP with CardCode {CardCode}, Lines: {LineCount}",
            invoiceId, sapInvoice.CardCode, sapInvoice.DocumentLines?.Count ?? 0);

        var existingCreditNotes = await GetByInvoiceIdAsync(invoiceId, cancellationToken);
        var activeCreditedAmount = CalculateActiveCreditedAmount(existingCreditNotes);
        var requestedCreditAmount = CalculateCreditNoteRequestTotal(lines);
        var remainingCreditableAmount = Math.Max(0m, sapInvoice.DocTotal - activeCreditedAmount);
        var currency = string.IsNullOrWhiteSpace(sapInvoice.DocCurrency) ? "USD" : sapInvoice.DocCurrency;

        if (remainingCreditableAmount <= CreditAmountTolerance)
        {
            throw new InvalidOperationException(
                $"Invoice #{sapInvoice.DocNum} has already been fully credited. No additional credit note can be created.");
        }

        if (requestedCreditAmount > remainingCreditableAmount + CreditAmountTolerance)
        {
            throw new InvalidOperationException(
                $"Requested credit amount {requestedCreditAmount:N2} {currency} exceeds the remaining creditable amount {remainingCreditableAmount:N2} {currency} for invoice #{sapInvoice.DocNum}.");
        }

        // Log invoice lines for debugging
        if (sapInvoice.DocumentLines != null)
        {
            foreach (var invLine in sapInvoice.DocumentLines)
            {
                _logger.LogInformation("Invoice line {LineNum}: Item={ItemCode}, Qty={Qty}, BatchCount={BatchCount}",
                    invLine.LineNum, invLine.ItemCode, invLine.Quantity, invLine.BatchNumbers?.Count ?? 0);

                if (invLine.BatchNumbers != null)
                {
                    foreach (var batch in invLine.BatchNumbers)
                    {
                        _logger.LogInformation("  Batch: {BatchNumber}, Qty={Qty}", batch.BatchNumber, batch.Quantity);
                    }
                }
            }
        }

        // Try to find local invoice ID for FK reference
        var localInvoice = await _context.Invoices
            .FirstOrDefaultAsync(i => i.SAPDocEntry == invoiceId, cancellationToken);

        // Enrich credit note lines with batch numbers from original invoice
        var enrichedLines = new List<CreateCreditNoteLineRequest>();
        foreach (var line in lines)
        {
            _logger.LogInformation("Processing credit line: Item={ItemCode}, OriginalLineId={LineId}",
                line.ItemCode, line.OriginalInvoiceLineId);

            // Match by LineNum first, then fallback to ItemCode
            var invoiceLine = sapInvoice.DocumentLines?.FirstOrDefault(l => l.LineNum == line.OriginalInvoiceLineId);

            if (invoiceLine == null)
            {
                // Fallback: match by ItemCode when LineNum doesn't match
                invoiceLine = sapInvoice.DocumentLines?.FirstOrDefault(l =>
                    string.Equals(l.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase));

                if (invoiceLine != null)
                {
                    _logger.LogInformation("Matched invoice line by ItemCode {ItemCode} (LineNum {OriginalLineId} not found, using LineNum {MatchedLineNum})",
                        line.ItemCode, line.OriginalInvoiceLineId, invoiceLine.LineNum);
                    // Update the line reference to the matched line
                    line.OriginalInvoiceLineId = invoiceLine.LineNum;
                }
                else
                {
                    _logger.LogWarning("Could not find invoice line for item {ItemCode} (LineNum={LineNum}). Available lines: {AvailableLines}",
                        line.ItemCode, line.OriginalInvoiceLineId,
                        string.Join(", ", sapInvoice.DocumentLines?.Select(l => $"{l.LineNum}:{l.ItemCode}") ?? Array.Empty<string>()));
                }
            }

            var enrichedLine = new CreateCreditNoteLineRequest
            {
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription ?? invoiceLine?.ItemDescription,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                TaxPercent = line.TaxPercent,
                WarehouseCode = line.WarehouseCode ?? invoiceLine?.WarehouseCode,
                ReturnReason = line.ReturnReason,
                BatchNumber = line.BatchNumber,
                OriginalInvoiceLineId = line.OriginalInvoiceLineId
            };

            // Copy batch numbers from the original invoice line
            if (invoiceLine?.BatchNumbers != null && invoiceLine.BatchNumbers.Any())
            {
                var batchesWithNumbers = invoiceLine.BatchNumbers
                    .Where(b => !string.IsNullOrEmpty(b.BatchNumber))
                    .ToList();

                if (batchesWithNumbers.Any())
                {
                    // Scale batch quantities proportionally if partial return
                    var originalQty = invoiceLine.Quantity;
                    var returnQty = line.Quantity;
                    var ratio = originalQty != 0 ? returnQty / originalQty : 1m;

                    enrichedLine.BatchNumbers = batchesWithNumbers
                        .Select(b => new CreditNoteBatchRequest
                        {
                            BatchNumber = b.BatchNumber,
                            Quantity = Math.Round(b.Quantity * ratio, 4)
                        })
                        .ToList();

                    _logger.LogInformation("Added {BatchCount} batch numbers to credit note line for item {ItemCode}: {Batches}",
                        enrichedLine.BatchNumbers.Count, line.ItemCode,
                        string.Join(", ", enrichedLine.BatchNumbers.Select(b => $"{b.BatchNumber}:{b.Quantity}")));
                }
                else
                {
                    _logger.LogWarning("Invoice line {LineNum} for item {ItemCode} has BatchNumbers collection but all entries have empty batch numbers",
                        invoiceLine.LineNum, line.ItemCode);
                }
            }
            else
            {
                _logger.LogWarning("No batch numbers found for item {ItemCode} on invoice line {LineNum}. BatchNumbers is {State}",
                    line.ItemCode, line.OriginalInvoiceLineId,
                    invoiceLine?.BatchNumbers == null ? "null" : "empty");
            }

            enrichedLines.Add(enrichedLine);
        }

        var request = new CreateCreditNoteRequest
        {
            CardCode = sapInvoice.CardCode ?? "",
            CardName = sapInvoice.CardName,
            Type = CreditNoteType.Return,
            OriginalInvoiceId = localInvoice?.Id, // Local DB ID (null if not found locally)
            OriginalInvoiceDocEntry = sapInvoice.DocEntry, // SAP DocEntry for reference
            Reason = reason,
            Currency = sapInvoice.DocCurrency,
            RestockItems = true,
            Lines = enrichedLines
        };

        return await CreateAsync(request, userId, cancellationToken);
    }

    public async Task<CreditNoteDto> UpdateStatusAsync(int id, CreditNoteStatus status, Guid userId, CancellationToken cancellationToken = default)
    {
        var creditNote = await _context.CreditNotes
            .Include(c => c.Lines)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (creditNote == null)
            throw new InvalidOperationException($"Credit note with ID {id} not found");

        creditNote.Status = status;
        creditNote.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(creditNote);
    }

    public async Task<CreditNoteDto> ApproveAsync(int id, Guid userId, CancellationToken cancellationToken = default)
    {
        var creditNote = await _context.CreditNotes
            .Include(c => c.Lines)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (creditNote == null)
            throw new InvalidOperationException($"Credit note with ID {id} not found");

        if (creditNote.Status != CreditNoteStatus.Pending)
            throw new InvalidOperationException("Only pending credit notes can be approved");

        creditNote.Status = CreditNoteStatus.Approved;
        creditNote.ApprovedByUserId = userId;
        creditNote.ApprovedDate = DateTime.UtcNow;
        creditNote.UpdatedAt = DateTime.UtcNow;

        // Process restocking if enabled
        if (creditNote.RestockItems)
        {
            foreach (var line in creditNote.Lines.Where(l => !l.IsRestocked))
            {
                // Update product stock
                var product = await _context.Products
                    .FirstOrDefaultAsync(p => p.ItemCode == line.ItemCode, cancellationToken);

                if (product != null)
                {
                    product.QuantityOnStock += line.Quantity;
                    line.IsRestocked = true;
                }
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Approved credit note {CreditNoteNumber}", creditNote.CreditNoteNumber);
        return MapToDto(creditNote);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var creditNote = await _context.CreditNotes.FindAsync(new object[] { id }, cancellationToken);
        if (creditNote == null)
            return false;

        if (creditNote.Status != CreditNoteStatus.Draft && creditNote.Status != CreditNoteStatus.Cancelled)
            throw new InvalidOperationException("Only draft or cancelled credit notes can be deleted");

        _context.CreditNotes.Remove(creditNote);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<string> GenerateCreditNoteNumberAsync(CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"CN-{today}-";

        var lastCreditNote = await _context.CreditNotes
            .Where(c => c.CreditNoteNumber.StartsWith(prefix))
            .OrderByDescending(c => c.CreditNoteNumber.Length)
            .ThenByDescending(c => c.CreditNoteNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1L;
        if (lastCreditNote != null)
        {
            var lastSequence = lastCreditNote.CreditNoteNumber.Replace(prefix, "");
            if (long.TryParse(lastSequence, out var parsed))
                sequence = parsed + 1;
        }

        return $"{prefix}{sequence:D4}";
    }

    private static CreditNoteDto MapToDto(CreditNoteEntity entity)
    {
        return new CreditNoteDto
        {
            Id = entity.Id,
            SAPDocEntry = entity.SAPDocEntry,
            SAPDocNum = entity.SAPDocNum,
            CreditNoteNumber = entity.CreditNoteNumber,
            CreditNoteDate = entity.CreditNoteDate,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            Type = entity.Type,
            Status = entity.Status,
            OriginalInvoiceId = entity.OriginalInvoiceId,
            OriginalInvoiceDocEntry = entity.OriginalInvoiceDocEntry,
            OriginalInvoiceSAPDocEntry = entity.OriginalInvoice?.SAPDocEntry,
            OriginalInvoiceSAPDocNum = entity.OriginalInvoice?.SAPDocNum,
            Reason = entity.Reason,
            Comments = entity.Comments,
            Currency = entity.Currency,
            ExchangeRate = entity.ExchangeRate,
            SubTotal = entity.SubTotal,
            TaxAmount = entity.TaxAmount,
            DocTotal = entity.DocTotal,
            AppliedAmount = entity.AppliedAmount,
            Balance = entity.Balance,
            RestockItems = entity.RestockItems,
            RestockWarehouseCode = entity.RestockWarehouseCode,
            CreatedByUserId = entity.CreatedByUserId,
            CreatedByUserName = entity.CreatedByUser?.Username,
            ApprovedByUserId = entity.ApprovedByUserId,
            ApprovedByUserName = entity.ApprovedByUser?.Username,
            ApprovedDate = entity.ApprovedDate,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            IsSynced = entity.IsSynced,
            Lines = entity.Lines.Select(l => new CreditNoteLineDto
            {
                Id = l.Id,
                LineNum = l.LineNum,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                LineTotal = l.LineTotal,
                WarehouseCode = l.WarehouseCode,
                ReturnReason = l.ReturnReason,
                BatchNumber = l.BatchNumber,
                IsRestocked = l.IsRestocked
            }).ToList()
        };
    }

    private static CreditNoteDto MapFromSAP(SAPCreditNote sap)
    {
        DateTime.TryParse(sap.DocDate, out var docDate);
        var originalInvoiceDocEntry = ResolveOriginalInvoiceDocEntry(sap);

        return new CreditNoteDto
        {
            Id = sap.DocEntry,
            SAPDocEntry = sap.DocEntry,
            SAPDocNum = sap.DocNum,
            CreditNoteNumber = $"SAP-CN-{sap.DocNum}",
            CreditNoteDate = docDate,
            CardCode = sap.CardCode ?? string.Empty,
            CardName = sap.CardName,
            Type = CreditNoteType.Return, // Default type for SAP credit notes
            Status = MapSAPStatusToLocal(sap.DocumentStatus, sap.Cancelled),
            OriginalInvoiceDocEntry = originalInvoiceDocEntry,
            OriginalInvoiceSAPDocEntry = originalInvoiceDocEntry,
            Reason = sap.Comments,
            Comments = sap.Comments,
            Currency = sap.DocCurrency,
            ExchangeRate = 1,
            SubTotal = sap.DocTotal - sap.VatSum,
            TaxAmount = sap.VatSum,
            DocTotal = sap.DocTotal,
            AppliedAmount = 0,
            Balance = sap.DocTotal,
            IsSynced = true,
            Lines = sap.DocumentLines?.Select(l => new CreditNoteLineDto
            {
                LineNum = l.LineNum,
                ItemCode = l.ItemCode ?? string.Empty,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent ?? 0,
                TaxCode = l.TaxCode,
                LineTotal = l.LineTotal,
                WarehouseCode = l.WarehouseCode,
                IsRestocked = false
            }).ToList() ?? new List<CreditNoteLineDto>()
        };
    }

    private static int? ResolveOriginalInvoiceDocEntry(SAPCreditNote sap)
        => sap.BaseEntry
        ?? sap.DocumentLines?
            .FirstOrDefault(line => line.BaseType == 13 && line.BaseEntry.HasValue)
            ?.BaseEntry;

    private static DateTime? NormalizeUtcDateStart(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    }

    private static DateTime? NormalizeUtcDateExclusiveEnd(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return DateTime.SpecifyKind(value.Value.Date.AddDays(1), DateTimeKind.Utc);
    }

    private static CreditNoteStatus MapSAPStatusToLocal(string? documentStatus, string? cancelled)
    {
        if (cancelled == "tYES")
            return CreditNoteStatus.Cancelled;

        return documentStatus switch
        {
            "bost_Open" => CreditNoteStatus.Approved,
            "bost_Close" => CreditNoteStatus.Applied,
            _ => CreditNoteStatus.Draft
        };
    }

    private static decimal CalculateCreditNoteRequestTotal(IEnumerable<CreateCreditNoteLineRequest> lines)
    {
        return lines.Sum(line =>
        {
            var lineTotal = line.Quantity * line.UnitPrice * (1 - line.DiscountPercent / 100);
            var lineTax = lineTotal * line.TaxPercent / 100;
            return lineTotal + lineTax;
        });
    }

    private static decimal CalculateActiveCreditedAmount(IEnumerable<CreditNoteDto> creditNotes)
    {
        return creditNotes
            .Where(IsActiveCreditNote)
            .Sum(note => note.DocTotal);
    }

    private static bool IsActiveCreditNote(CreditNoteDto creditNote)
    {
        return creditNote.Status != CreditNoteStatus.Cancelled;
    }

    private Task CaptureCreditNoteFiscalizationIncidentAsync(
        string reference,
        int? sapDocNum,
        string cardCode,
        string message,
        CancellationToken cancellationToken)
        => Features.CreditNotes.CreditNoteFiscalisationIncidents.CaptureAsync(
            _context, _logger, reference, sapDocNum, cardCode, message, cancellationToken);

}
