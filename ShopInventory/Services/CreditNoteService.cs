using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service implementation for Credit Note operations - Fetches from SAP Business One
/// </summary>
public class CreditNoteService : ICreditNoteService
{
    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly IFiscalizationService _fiscalizationService;
    private readonly ILogger<CreditNoteService> _logger;

    public CreditNoteService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        IFiscalizationService fiscalizationService,
        ILogger<CreditNoteService> logger)
    {
        _context = context;
        _sapClient = sapClient;
        _fiscalizationService = fiscalizationService;
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
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Fetch from SAP
            _logger.LogInformation("Fetching credit notes from SAP - Page: {Page}, PageSize: {PageSize}", page, pageSize);

            List<SAPCreditNote> sapCreditNotes;
            int totalCount;

            if (!string.IsNullOrEmpty(cardCode))
            {
                sapCreditNotes = await _sapClient.GetCreditNotesByCustomerAsync(cardCode, cancellationToken);
                totalCount = sapCreditNotes.Count;
                sapCreditNotes = sapCreditNotes.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            }
            else if (fromDate.HasValue && toDate.HasValue)
            {
                sapCreditNotes = await _sapClient.GetCreditNotesByDateRangeAsync(fromDate.Value, toDate.Value, cancellationToken);
                totalCount = sapCreditNotes.Count;
                sapCreditNotes = sapCreditNotes.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            }
            else
            {
                sapCreditNotes = await _sapClient.GetPagedCreditNotesAsync(page, pageSize, cancellationToken);
                totalCount = await _sapClient.GetCreditNotesCountAsync(cardCode, fromDate, toDate, cancellationToken);
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

    private async Task<CreditNoteListResponseDto> GetAllFromLocalAsync(int page, int pageSize, CreditNoteStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        var query = _context.CreditNotes
            .Include(c => c.Lines)
            .Include(c => c.CreatedByUser)
            .AsNoTracking()
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(c => c.Status == status.Value);

        if (!string.IsNullOrEmpty(cardCode))
            query = query.Where(c => c.CardCode == cardCode);

        if (fromDate.HasValue)
            query = query.Where(c => c.CreditNoteDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(c => c.CreditNoteDate <= toDate.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var creditNotes = await query
            .OrderByDescending(c => c.CreditNoteDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new CreditNoteListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            CreditNotes = creditNotes.Select(MapToDto).ToList()
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

        // FISCALIZE with REVMax after successful SAP posting
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
                }
            }
            else
            {
                _logger.LogWarning(
                    "Cannot fiscalize credit note {DocNum}: No original invoice reference",
                    sapCreditNote.DocNum);
            }
        }
        catch (Exception fiscalEx)
        {
            _logger.LogError(fiscalEx,
                "Error during fiscalization of credit note {DocNum}. Credit note was created in SAP.",
                sapCreditNote.DocNum);
        }

        // Save to local database only after successful SAP posting
        _context.CreditNotes.Add(creditNote);
        await _context.SaveChangesAsync(cancellationToken);

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

            var invoiceLine = sapInvoice.DocumentLines?.FirstOrDefault(l => l.LineNum == line.OriginalInvoiceLineId);

            if (invoiceLine == null)
            {
                _logger.LogWarning("Could not find invoice line {LineNum} for item {ItemCode}",
                    line.OriginalInvoiceLineId, line.ItemCode);
            }

            var enrichedLine = new CreateCreditNoteLineRequest
            {
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
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
                // Scale batch quantities proportionally if partial return
                var originalQty = invoiceLine.Quantity;
                var returnQty = line.Quantity;
                var ratio = returnQty / originalQty;

                enrichedLine.BatchNumbers = invoiceLine.BatchNumbers
                    .Where(b => !string.IsNullOrEmpty(b.BatchNumber))
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
                _logger.LogWarning("No batch numbers found for item {ItemCode} on invoice line {LineNum}",
                    line.ItemCode, line.OriginalInvoiceLineId);
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
            .OrderByDescending(c => c.CreditNoteNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1;
        if (lastCreditNote != null)
        {
            var lastSequence = lastCreditNote.CreditNoteNumber.Replace(prefix, "");
            if (int.TryParse(lastSequence, out int parsed))
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
            OriginalInvoiceDocEntry = sap.BaseEntry,
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
                DiscountPercent = l.DiscountPercent,
                LineTotal = l.LineTotal,
                WarehouseCode = l.WarehouseCode,
                IsRestocked = false
            }).ToList() ?? new List<CreditNoteLineDto>()
        };
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
}
