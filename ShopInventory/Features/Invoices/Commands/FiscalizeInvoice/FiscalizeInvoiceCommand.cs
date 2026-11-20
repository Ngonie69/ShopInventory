using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;

/// <param name="DocEntry">SAP's internal key of the invoice to fiscalise.</param>
/// <param name="UserId">The user who pressed Fiscalise, named to the platform as the receipt's operator. Null for the scheduled retry.</param>
/// <param name="Username">That user's username, for the log and the operator name. Null for the scheduled retry.</param>
/// <param name="SourceSystem">
/// The source the attempt's fiscal transaction row is recorded under. Null for a person pressing Fiscalise;
/// the scheduled retry passes its own, which is how it counts its attempts and how the log tells them apart.
/// </param>
public sealed record FiscalizeInvoiceCommand(
    int DocEntry,
    Guid? UserId,
    string? Username,
    string? SourceSystem = null) : IRequest<ErrorOr<FiscalizationResult>>;
