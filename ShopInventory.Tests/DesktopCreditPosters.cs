using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Revmax;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Builds the SAP poster that the credit-note service and the sale-posting service both take.
/// </summary>
/// <remarks>
/// Two callers reach it for different reasons, and neither is what their tests are about. The credit
/// service calls it the moment ZIMRA accepts a credit; the posting pass calls it once per sale it puts
/// in SAP. In a fiscal test the sale has no SAP invoice, so the poster establishes that and returns
/// without sending anything; in a posting test there are no credits at all. Either way nothing here is
/// asked to talk to SAP — which is why that stub is the unused kind. A test that reached it would get
/// an exception naming the call, which is the right answer: it would mean a document was about to be
/// raised that the test never set up.
/// </remarks>
internal static class DesktopCreditPosters
{
    public static DesktopCreditSapPoster Idle(ApplicationDbContext context)
        => new(
            context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            NoLedger(),
            NoAudit(),
            Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.DesktopSalePostingSettings()),
            NullLogger<DesktopCreditSapPoster>.Instance);

    /// <summary>
    /// A poster that answers, and records what it was asked to raise.
    /// </summary>
    /// <remarks>
    /// For the tests whose subject is the hook rather than the memo — a van sale posting, a queue entry
    /// completing — where <see cref="Idle"/> would prove only that nothing reached SAP, which is also
    /// what a hook that never fired proves.
    /// </remarks>
    public static DesktopCreditSapPoster Recording(
        ApplicationDbContext context, RecordingSap sap, RecordingLedger? ledger = null)
        => new(
            context,
            sap.Client,
            (ledger ?? new RecordingLedger()).Ledger,
            NoAudit(),
            Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.DesktopSalePostingSettings()),
            NullLogger<DesktopCreditSapPoster>.Instance);

    /// <summary>
    /// A credit against one line of a sale, in the state ZIMRA has accepted and SAP has not been asked
    /// for.
    /// </summary>
    /// <remarks>
    /// Built through the real <see cref="DesktopCreditPlan"/> and serialised exactly as the fiscal
    /// service stores it — the poster reads that JSON back, so a hand-rolled shape would test nothing.
    /// </remarks>
    public static async Task<DesktopCreditNoteEntity> GivenCreditAsync(
        ApplicationDbContext context,
        DesktopSaleEntity sale,
        string status = DesktopCreditStatuses.Fiscalised,
        int creditedLineNum = 0,
        decimal quantity = 2m,
        string? receiptLineName = null)
    {
        var saleLine = sale.Lines.FirstOrDefault(line => line.LineNum == creditedLineNum);

        var source = new DesktopCreditSource(
            sale.ExternalReferenceId, sale.Currency, sale.TotalAmount, 22862, 525, 216877, null,
            [
                new DesktopCreditLine(
                    creditedLineNum,
                    receiptLineName ?? saleLine?.ItemDescription ?? "Unknown",
                    saleLine?.Quantity ?? quantity,
                    10m, 7, 15.5m, "O01", null)
            ]);

        var plan = new DesktopCreditPlan(
            source,
            [new DesktopCreditQuantity(creditedLineNum, quantity)],
            new SubmitReceiptApiRequest { InvoiceNo = "DCN-test", ReceiptType = ReceiptType.CreditNote },
            quantity * 10m);

        var note = new DesktopCreditNoteEntity
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            RequestKey = Guid.NewGuid().ToString("N"),
            RequestHash = "hash",
            Number = $"DCN-{Guid.NewGuid():N}",
            OriginalFiscalNumber = sale.ExternalReferenceId,
            Reason = "Customer return",
            Currency = sale.Currency,
            Amount = plan.Amount,
            Status = status,
            PlanJson = JsonSerializer.Serialize(plan, DesktopCreditNoteService.Json),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = Guid.NewGuid()
        };

        context.DesktopCreditNotes.Add(note);
        await context.SaveChangesAsync();
        context.Entry(note).State = EntityState.Detached;
        return note;
    }

    /// <summary>
    /// A ledger that takes returns and records nothing. Returning units is deliberately best-effort in
    /// the poster — a ledger that refuses must never fail a credit ZIMRA has already accepted — so a
    /// throwing stub would be swallowed and prove nothing either way.
    /// </summary>
    private static IStockLedger NoLedger()
        => StubProxy.For<IStockLedger>((method, _) => method.Name switch
        {
            nameof(IStockLedger.ReleaseAsync) => Task.CompletedTask,
            _ => throw new InvalidOperationException($"IStockLedger.{method.Name} was not expected.")
        });

    private static IAuditService NoAudit()
        => StubProxy.For<IAuditService>((method, _) => method.Name == nameof(IAuditService.LogAsync)
            ? Task.CompletedTask
            : throw new InvalidOperationException($"IAuditService.{method.Name} was not expected."));
}

internal sealed class RecordingSap
{
    private int _nextDocNum = 88001;

    public List<CreateCreditNoteRequest> Created { get; } = [];

    public Dictionary<string, SAPCreditNote> ExistingByReference { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        method.Name switch
        {
            // Cast to object so the switch's natural type is not taken from the arm below, which
            // answers a non-nullable Task — this lookup's contract is that it may answer null.
            nameof(ISAPServiceLayerClient.GetCreditNoteByReferenceAsync) =>
                (object)Task.FromResult(
                    ExistingByReference.TryGetValue((string)args![0]!, out var found) ? found : null),

            nameof(ISAPServiceLayerClient.CreateCreditNoteAsync) =>
                Create((CreateCreditNoteRequest)args![0]!),

            _ => throw new InvalidOperationException(
                $"ISAPServiceLayerClient.{method.Name} was not expected on this path.")
        });

    private Task<SAPCreditNote> Create(CreateCreditNoteRequest request)
    {
        Created.Add(request);
        var docNum = _nextDocNum++;
        return Task.FromResult(new SAPCreditNote
        {
            DocEntry = docNum, DocNum = docNum, CardCode = request.CardCode, NumAtCard = request.SapReference
        });
    }
}

internal sealed class RecordingLedger
{
    public List<StockLedgerLine> Released { get; } = [];

    public IStockLedger Ledger => StubProxy.For<IStockLedger>((method, args) => method.Name switch
    {
        nameof(IStockLedger.ReleaseAsync) => Record((IReadOnlyList<StockLedgerLine>)args![0]!),
        _ => throw new InvalidOperationException($"IStockLedger.{method.Name} was not expected.")
    });

    private Task Record(IReadOnlyList<StockLedgerLine> lines)
    {
        Released.AddRange(lines);
        return Task.CompletedTask;
    }
}
