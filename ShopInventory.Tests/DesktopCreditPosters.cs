using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Services;

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
            NullLogger<DesktopCreditSapPoster>.Instance);

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
