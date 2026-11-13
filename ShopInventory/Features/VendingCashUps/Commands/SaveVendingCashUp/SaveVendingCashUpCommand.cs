using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VendingCashUps.Commands.SaveVendingCashUp;

/// <summary>
/// Keys or re-keys one cashier's count for one day at one depot warehouse.
/// </summary>
/// <remarks>
/// <para>
/// A second save of the same cashier, warehouse and day replaces the first until it is approved.
/// <see cref="ExpectedRevision"/> is the revision the form was opened on (0 for a day with no count),
/// and the save is refused if the row has moved on since.
/// </para>
/// <para>
/// A variance does not stop a save — the count is what it is — but it stops the approval until
/// <see cref="VarianceReason"/> says why.
/// </para>
/// </remarks>
public sealed record SaveVendingCashUpCommand(
    Guid CashierUserId,
    string WarehouseCode,
    DateTime BusinessDate,
    int ExpectedRevision,
    IReadOnlyList<SaveVendingCashUpCurrency> Currencies,
    IReadOnlyList<SaveVendingCashUpTender> Tenders,
    string? VarianceReason,
    string? Note,
    Guid? ActingUserId
) : IRequest<ErrorOr<VendingCashUpSheetDto>>;

/// <summary>
/// The cash in one currency. Give the notes and coins in <see cref="Denominations"/> and the count is
/// their sum; give <see cref="CountedCash"/> alone and it is taken as typed; give both and they must agree.
/// </summary>
public sealed record SaveVendingCashUpCurrency(
    string Currency,
    decimal OpeningFloat,
    decimal? CountedCash,
    decimal Banked,
    string? DepositReference,
    IReadOnlyList<SaveVendingCashUpDenomination>? Denominations);

public sealed record SaveVendingCashUpTender(string Currency, string Tender, decimal Declared);

public sealed record SaveVendingCashUpDenomination(decimal Value, int Quantity);
