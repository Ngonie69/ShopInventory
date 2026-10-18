using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;

/// <param name="SourceSystem">
/// The source the attempt's fiscal transaction row is recorded under. Null for a person pressing Fiscalise;
/// the scheduled retry passes its own, which is how it counts its attempts and how the log tells them apart.
/// </param>
public sealed record FiscalizeInvoiceCommand(
    int DocEntry,
    Guid? UserId,
    string? Username,
    string? SourceSystem = null) : IRequest<ErrorOr<FiscalizationResult>>;
