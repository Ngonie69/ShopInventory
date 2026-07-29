using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;

public sealed record FiscalizeInvoiceCommand(
    int DocEntry,
    Guid? UserId,
    string? Username) : IRequest<ErrorOr<FiscalizationResult>>;