using ErrorOr;
using MediatR;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesDocuments.Commands.PostVanSalesInvoiceToSap;

/// <summary>
/// Post one van invoice to SAP now, by its van order — the same request Desktop Sales makes for a held
/// till sale, answered by the same endpoint.
/// </summary>
public sealed record PostVanSalesInvoiceToSapCommand(string Reference)
    : IRequest<ErrorOr<DesktopSalePostResultDto>>;
