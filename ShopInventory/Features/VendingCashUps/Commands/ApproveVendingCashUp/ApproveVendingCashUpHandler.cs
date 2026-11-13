using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VendingCashUps.Commands.ApproveVendingCashUp;

/// <summary>
/// Approves a count, checking the rules in the order a person would want to hear them: who you are,
/// whether the count is still the one you saw, then whether it is ready.
/// </summary>
/// <remarks>
/// The approver's role is read from their account, not the token: a portal call carries the API key's
/// identity beside the user's, and a whole-principal role check answers for the key.
/// </remarks>
public sealed class ApproveVendingCashUpHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<ApproveVendingCashUpHandler> logger
) : IRequestHandler<ApproveVendingCashUpCommand, ErrorOr<VendingCashUpSheetDto>>
{
    private static readonly string[] ApprovingRoles = [ApplicationRoles.Admin];

    public async Task<ErrorOr<VendingCashUpSheetDto>> Handle(
        ApproveVendingCashUpCommand command,
        CancellationToken cancellationToken)
    {
        var (actor, isAdmin) = await VendingCashUpSheets.ActorAsync(db, command.ActingUserId, ApprovingRoles, cancellationToken);
        if (actor is null)
        {
            return Errors.VendingCashUp.SignedInUserRequired;
        }

        if (!isAdmin)
        {
            return Errors.VendingCashUp.ApproverMustBeAdmin;
        }

        var cashUp = await VendingCashUpSheets.FindAsync(db, command.CashUpId, tracking: true, cancellationToken);
        if (cashUp is null)
        {
            return Errors.VendingCashUp.NotFound;
        }

        if (VendingCashUpSheets.SavedBy(cashUp).Contains(actor.Id))
        {
            return Errors.VendingCashUp.CannotApproveOwnCount;
        }

        if (cashUp.Revision != command.ExpectedRevision)
        {
            return Errors.VendingCashUp.Stale;
        }

        if (cashUp.Status == VendingCashUpStatuses.Approved)
        {
            return Errors.VendingCashUp.AlreadyApproved;
        }

        if (cashUp.Status != VendingCashUpStatuses.Submitted)
        {
            return Errors.VendingCashUp.NotAwaitingApproval(cashUp.Status);
        }

        var sales = await VendingCashUpExpectations.LoadAsync(
            db, cashUp.BusinessDate, cashUp.BusinessDate, cashUp.CashierUserId, cashUp.WarehouseCode, cancellationToken);
        var reading = VendingCashUpReading.Read(cashUp, sales, VendingCashUpReading.DepotCurrencies);

        if (reading.SalesChangedSinceCount)
        {
            return Errors.VendingCashUp.SalesChangedSinceCount;
        }

        if (reading.NeedsReason)
        {
            return Errors.VendingCashUp.ReasonRequired;
        }

        cashUp.Status = VendingCashUpStatuses.Approved;
        cashUp.ApprovedByUserId = actor.Id;
        cashUp.ApprovedByName = actor.Username;
        cashUp.ApprovedAt = DateTime.UtcNow;
        cashUp.Revision += 1;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Vending cash-up {CashUpId} for {Cashier} on {Date:yyyy-MM-dd} approved by {Actor} (balanced {Balanced})",
            cashUp.Id, cashUp.CashierUsername, cashUp.BusinessDate, actor.Username, reading.Balanced);

        await LogAsync(cashUp, reading.Balanced);

        var directory = await VendingCashUpDirectory.LoadAsync(db, [cashUp.CashierUserId], cancellationToken);
        return VendingCashUpSheets.Build(
            cashUp.CashierUserId, cashUp.WarehouseCode, cashUp.BusinessDate, cashUp, sales, directory, actor.Id);
    }

    private async Task LogAsync(VendingCashUpEntity cashUp, bool balanced)
    {
        try
        {
            await auditService.LogAsync(
                "VendingCashUpApproved",
                "VendingCashUp",
                cashUp.Id.ToString(),
                $"Approved the cash-up of {cashUp.CashierUsername} at {cashUp.WarehouseCode} for {cashUp.BusinessDate:dd MMM yyyy}" +
                (balanced ? " (balanced)." : $" with a variance. Reason: {cashUp.VarianceReason}"),
                true);
        }
        catch
        {
        }
    }
}
