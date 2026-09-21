using ErrorOr;
using MediatR;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesDocuments.Commands.RetryVanSalesInvoiceFiscalisation;

/// <summary>
/// Ask the fiscal device to sign a van sale whose fiscalisation failed, now — the same request Desktop
/// Sales makes for a till sale, answered by the same endpoint, which asks the device for an existing
/// receipt before sending anything.
/// </summary>
public sealed record RetryVanSalesInvoiceFiscalisationCommand(string Reference)
    : IRequest<ErrorOr<DesktopSaleFiscalisationRetryResultDto>>;
