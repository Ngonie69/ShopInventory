using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VendingCashUps.Commands.ApproveVendingCashUp;

/// <summary>
/// An Admin accepts a count. Refused to anyone who saved it, while sales have changed since it was
/// saved, and while it shows a variance with no reason.
/// </summary>
public sealed record ApproveVendingCashUpCommand(
    int CashUpId,
    int ExpectedRevision,
    Guid? ActingUserId
) : IRequest<ErrorOr<VendingCashUpSheetDto>>;
