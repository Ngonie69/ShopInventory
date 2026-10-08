using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveries;

/// <summary>Every WhatsApp send of one document, newest first.</summary>
public sealed record GetCustomerDocumentDeliveriesQuery(
    int? SapDocEntry,
    int? DesktopSaleId) : IRequest<ErrorOr<List<CustomerDocumentDeliveryDto>>>;
