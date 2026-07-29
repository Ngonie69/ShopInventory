using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;

public sealed record GetInvoiceByDocNumQuery(
	int DocNum,
	Guid? RequestingUserId = null,
	bool RestrictToAssignedCustomers = false
) : IRequest<ErrorOr<InvoiceDto>>;
