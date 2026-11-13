using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VendingCashUps.Commands.QueryVendingCashUp;

/// <summary>An Admin sends a count back with a question. It can be approved once it is saved again.</summary>
public sealed record QueryVendingCashUpCommand(
    int CashUpId,
    int ExpectedRevision,
    string? Note,
    Guid? ActingUserId
) : IRequest<ErrorOr<VendingCashUpSheetDto>>;
