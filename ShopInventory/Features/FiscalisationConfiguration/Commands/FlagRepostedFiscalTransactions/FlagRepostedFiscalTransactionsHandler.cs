using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.FlagRepostedFiscalTransactions;

/// <summary>
/// Flags the log rows written for reposted invoices before the log could record a repost itself.
/// </summary>
/// <remarks>
/// The read-back now records <c>RepostedAfterSapUpdate</c> when it writes a row, from the invoice's
/// remarks. Rows it wrote before that carry nothing but the DocNum — not the remarks — so the only way to
/// recognise them is to ask SAP again. This does that once per node start, for "Not Fiscalised" invoice
/// rows synced since <see cref="FiscalisationSettings.RepostedInvoiceSweepSinceUtc"/>, fifty numbers to a
/// lookup.
///
/// Only "Not Fiscalised" rows, which are the read-back's own verdict. A failed or unresolved attempt
/// against a repost means someone sent it before the fiscalise route refused reposts, and that row must
/// stay in front of a person.
///
/// Idempotent, so every node may run it: a flagged row is not read again, and two nodes flagging the same
/// row write the same value. What it re-reads on each start is the ordinary unfiscalised invoices in the
/// window, which is why the window has an off switch.
/// </remarks>
public sealed class FlagRepostedFiscalTransactionsHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    ILogger<FlagRepostedFiscalTransactionsHandler> logger)
    : IRequestHandler<FlagRepostedFiscalTransactionsCommand, ErrorOr<FlagRepostedFiscalTransactionsResult>>
{
    private const string InvoiceDocumentType = "Invoice";
    private const string NotFiscalisedStatus = "Not Fiscalised";

    /// <summary>The chunk <see cref="ISAPServiceLayerClient.GetInvoicesByDocNumsAsync"/> sends in one request.</summary>
    private const int BatchSize = 50;

    public async Task<ErrorOr<FlagRepostedFiscalTransactionsResult>> Handle(
        FlagRepostedFiscalTransactionsCommand command,
        CancellationToken cancellationToken)
    {
        var settings = fiscalisationSettings.Value;

        if (!sapSettings.Value.Enabled
            || string.IsNullOrWhiteSpace(settings.RepostedInvoiceCommentsPrefix)
            || settings.RepostedInvoiceSweepSinceUtc is not { } since)
        {
            return new FlagRepostedFiscalTransactionsResult(Ran: false, InvoicesChecked: 0, RowsFlagged: 0);
        }

        // A date from configuration arrives Unspecified; it is a UTC date by the setting's name.
        var sinceUtc = since.Kind switch
        {
            DateTimeKind.Utc => since,
            DateTimeKind.Local => since.ToUniversalTime(),
            _ => DateTime.SpecifyKind(since, DateTimeKind.Utc)
        };

        var docNums = await Unflagged(db)
            .Where(transaction => transaction.LastSyncedAtUtc >= sinceUtc)
            .Select(transaction => transaction.DocNum)
            .Distinct()
            .OrderBy(docNum => docNum)
            .ToListAsync(cancellationToken);

        var invoicesChecked = 0;
        var rowsFlagged = 0;

        foreach (var batch in docNums.Chunk(BatchSize))
        {
            List<Models.Invoice> invoices;

            try
            {
                invoices = await sapClient.GetInvoicesByDocNumsAsync(batch, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // What is flagged so far stays flagged; the next start picks up the rest.
                logger.LogWarning(
                    ex,
                    "Stopped flagging reposted invoices in the fiscal transaction log after {Checked} of {Total} "
                    + "invoice(s) and {Flagged} row(s): SAP could not be read",
                    invoicesChecked,
                    docNums.Count,
                    rowsFlagged);
                return Errors.Invoice.SapConnectionError(ex.Message);
            }

            var reposted = invoices
                .Where(invoice => RepostedInvoiceMarker.IsReposted(settings, invoice.Comments))
                .Select(invoice => invoice.DocNum)
                .ToList();

            if (reposted.Count > 0)
            {
                rowsFlagged += await Unflagged(db)
                    .Where(transaction => reposted.Contains(transaction.DocNum))
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(transaction => transaction.RepostedAfterSapUpdate, true),
                        cancellationToken);
            }

            invoicesChecked += batch.Length;
        }

        logger.LogInformation(
            "Flagged {Flagged} fiscal transaction row(s) as reposted after the SAP update, from {Checked} invoice(s) "
            + "read back as not fiscalised since {Since:yyyy-MM-dd}",
            rowsFlagged,
            invoicesChecked,
            sinceUtc);

        return new FlagRepostedFiscalTransactionsResult(Ran: true, invoicesChecked, rowsFlagged);
    }

    private static IQueryable<Models.Entities.DesktopFiscalTransactionEntity> Unflagged(ApplicationDbContext db) =>
        db.DesktopFiscalTransactions.Where(transaction =>
            transaction.DocumentType == InvoiceDocumentType
            && transaction.Status == NotFiscalisedStatus
            && transaction.DocNum > 0
            && !transaction.RepostedAfterSapUpdate);
}
