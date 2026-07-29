using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetCreditNoteByDocNum;

public sealed record GetCreditNoteByDocNumQuery(
    int DocNum
) : IRequest<ErrorOr<CreditNoteDto>>;