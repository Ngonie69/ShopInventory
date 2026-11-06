using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.CreditNotes.Queries.GetCustomerCreditNotes;

/// <summary>
/// Reads a customer's credit memos for a period straight from SAP, lines included.
/// </summary>
/// <remarks>
/// Not from the credit-note projection the staff list reads: it keeps no quantity, no item
/// description and no amount used, and those are most of what a customer opens a credit note to see.
/// One bounded read covers every account named, so the cost follows the period, not the history.
/// </remarks>
public sealed class GetCustomerCreditNotesHandler(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetCustomerCreditNotesHandler> logger
) : IRequestHandler<GetCustomerCreditNotesQuery, ErrorOr<CustomerCreditNotesResult>>
{
    public async Task<ErrorOr<CustomerCreditNotesResult>> Handle(
        GetCustomerCreditNotesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
        {
            return Errors.CreditNote.SapDisabled;
        }

        var cardCodes = request.CardCodes
            .Where(cardCode => !string.IsNullOrWhiteSpace(cardCode))
            .Select(cardCode => cardCode.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<SAPCreditNote> read;
        try
        {
            read = await sapClient.GetCreditNotesByCustomersAsync(
                cardCodes, request.FromDate.Date, request.ToDate.Date, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not read the credit notes of {CardCodes} between {From:yyyy-MM-dd} and {To:yyyy-MM-dd} from SAP",
                string.Join(",", cardCodes), request.FromDate, request.ToDate);
            return Errors.CreditNote.SapReadFailed(ex.Message);
        }

        // SAP was asked for these accounts only; the second look keeps a reply that strayed — a filter
        // chunk gone wrong — from showing one customer another's credit.
        var creditNotes = read
            .Where(creditNote => creditNote.CardCode is { } code && cardCodes.Contains(code.Trim()))
            .Where(creditNote => !CreditNoteFigures.IsCancelledOrCancellation(creditNote))
            .GroupBy(creditNote => creditNote.DocEntry)
            .Select(group => group.First())
            .ToList();

        var creditedInvoices = await CreditedInvoiceNumbersAsync(creditNotes, cancellationToken);
        var receipts = await CreditNoteReceiptLookup.FindAsync(dbContext, creditNotes, cancellationToken);

        return new CustomerCreditNotesResult
        {
            CreditNotes = creditNotes
                .Select(creditNote => Map(
                    creditNote,
                    creditedInvoices.GetValueOrDefault(creditNote.DocEntry),
                    receipts.GetValueOrDefault(creditNote.DocEntry)))
                .OrderByDescending(creditNote => creditNote.DocDate)
                .ThenByDescending(creditNote => creditNote.DocNum)
                .ToList()
        };
    }

    /// <summary>
    /// The number of the invoice each memo credits, keyed by the memo's DocEntry. A memo whose base
    /// invoice is on another account gets none: the number is that account's, not this customer's.
    /// </summary>
    /// <remarks>
    /// A failure here costs the column, not the page — the credit notes are what was asked for.
    /// </remarks>
    private async Task<Dictionary<int, int>> CreditedInvoiceNumbersAsync(
        IReadOnlyCollection<SAPCreditNote> creditNotes,
        CancellationToken cancellationToken)
    {
        var baseEntries = creditNotes
            .Select(creditNote => (creditNote, invoiceDocEntry: CreditNoteFigures.CreditedInvoiceDocEntry(creditNote)))
            .Where(pair => pair.invoiceDocEntry is > 0)
            .ToList();

        var numbers = new Dictionary<int, int>();
        if (baseEntries.Count == 0)
        {
            return numbers;
        }

        try
        {
            var invoices = (await sapClient.GetInvoiceBalancesByDocEntriesAsync(
                    baseEntries.Select(pair => pair.invoiceDocEntry!.Value).Distinct(),
                    cancellationToken))
                .GroupBy(invoice => invoice.DocEntry)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var (creditNote, invoiceDocEntry) in baseEntries)
            {
                if (invoices.TryGetValue(invoiceDocEntry!.Value, out var invoice)
                    && string.Equals(invoice.CardCode?.Trim(), creditNote.CardCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    numbers[creditNote.DocEntry] = invoice.DocNum;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the invoices {Count} credit note(s) were raised against", baseEntries.Count);
        }

        return numbers;
    }

    private static CustomerCreditNoteDto Map(SAPCreditNote creditNote, int creditedInvoiceDocNum, CreditNoteReceipt? receipt)
    {
        // A SAP date is a calendar day. Read with no zone as UTC, it stays that day; read as local
        // time and adjusted, a CAT server would move it to the evening before.
        DateTime.TryParse(creditNote.DocDate, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var docDate);

        var total = CreditNoteFigures.Total(creditNote);
        var applied = CreditNoteFigures.Applied(creditNote);

        return new CustomerCreditNoteDto
        {
            DocEntry = creditNote.DocEntry,
            DocNum = creditNote.DocNum,
            DocDate = DateTime.SpecifyKind(docDate.Date, DateTimeKind.Utc),
            CardCode = creditNote.CardCode?.Trim() ?? string.Empty,
            CardName = creditNote.CardName,
            Currency = CreditNoteFigures.Currency(creditNote),
            DocTotal = total,
            VatTotal = CreditNoteFigures.Vat(creditNote),
            AppliedAmount = applied,
            CreditLeft = total - applied,
            CreditedInvoiceDocNum = creditedInvoiceDocNum,
            Reason = CreditNoteFigures.Reason(creditNote),
            IsFiscalised = receipt is not null,
            FiscalReceiptNumber = receipt?.ReceiptGlobalNo ?? 0,
            FiscalVerificationCode = VerificationCode(receipt?.VerificationCode),
            FiscalVerificationUrl = VerificationUrl(receipt?.QrCode),
            Lines = (creditNote.DocumentLines ?? [])
                .OrderBy(line => line.LineNum)
                .Select(line => MapLine(creditNote, line))
                .ToList()
        };
    }

    private static CustomerCreditNoteLineDto MapLine(SAPCreditNote creditNote, SAPCreditNoteLine line)
    {
        var gross = CreditNoteFigures.LineGross(creditNote, line);
        var lineTotal = gross != 0m ? gross : CreditNoteFigures.LineNet(creditNote, line);

        return new CustomerCreditNoteLineDto
        {
            ItemCode = line.ItemCode,
            Description = line.ItemDescription,
            Quantity = line.Quantity,
            UoMCode = line.UoMCode,
            // SAP's own gross unit price on a USD memo. On a foreign one the unit follows from the
            // converted total, so a line's price and total can never disagree on the page.
            UnitPrice = !Common.Sales.SapDocumentAmounts.IsForeignCurrency(creditNote) && line.PriceAfterVAT != 0m
                ? line.PriceAfterVAT
                : line.Quantity != 0m ? Math.Round(lineTotal / line.Quantity, 2) : lineTotal,
            LineTotal = lineTotal,
            Reason = string.IsNullOrWhiteSpace(line.CreditReason) ? null : line.CreditReason.Trim()
        };
    }

    private static string? VerificationCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        return trimmed.Contains('-') ? trimmed : FiscalReceiptQrComposer.FormatVerificationCode(trimmed);
    }

    /// <summary>The QR payload, when it is an address a browser can open.</summary>
    private static string? VerificationUrl(string? qrCode) =>
        Uri.TryCreate(qrCode?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : null;
}
