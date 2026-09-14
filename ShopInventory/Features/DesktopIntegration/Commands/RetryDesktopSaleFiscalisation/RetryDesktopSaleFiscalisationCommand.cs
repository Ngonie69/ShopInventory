using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryDesktopSaleFiscalisation;

/// <summary>
/// Asks the fiscal device to sign a sale whose fiscalisation failed, now, instead of waiting for the sweep.
/// </summary>
/// <remarks>
/// Keyed and scoped exactly as <c>PostDesktopSaleToSapCommand</c> is: the external reference is what the
/// console shows and what the receipt is filed under, and <c>CallerUserId</c> is what the warehouse scope
/// is resolved from, so a shop-scoped account cannot fiscalise another shop's sale.
/// </remarks>
public sealed record RetryDesktopSaleFiscalisationCommand(
    Guid CallerUserId,
    string ExternalReferenceId
) : IRequest<ErrorOr<DesktopSaleFiscalisationRetryResult>>;

/// <summary>What retrying one sale's fiscalisation did, and the receipt it left.</summary>
public sealed record DesktopSaleFiscalisationRetryResult(
    string ExternalReferenceId,
    string Outcome,
    string? FiscalReceiptNumber,
    string? Message
);

/// <summary>The values <see cref="DesktopSaleFiscalisationRetryResult.Outcome"/> takes.</summary>
public static class DesktopSaleFiscalisationRetryOutcomes
{
    /// <summary>The device signed a new receipt for this sale.</summary>
    public const string Fiscalised = "Fiscalised";

    /// <summary>
    /// The device already held a receipt for this sale — an earlier attempt reached it and its answer did
    /// not reach us — so that receipt was adopted and nothing was sent.
    /// </summary>
    public const string AlreadyFiscalised = "AlreadyFiscalised";
}
