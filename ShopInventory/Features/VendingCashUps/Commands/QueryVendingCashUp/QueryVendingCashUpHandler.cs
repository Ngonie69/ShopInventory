using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.VendingCashUps.Commands.QueryVendingCashUp;

/// <summary>Sends a submitted count back to be answered, with the Admin's question kept on it.</summary>
public sealed class QueryVendingCashUpHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<QueryVendingCashUpHandler> logger
) : IRequestHandler<QueryVendingCashUpCommand, ErrorOr<VendingCashUpSheetDto>>
{
    private static readonly string[] ApprovingRoles = [ApplicationRoles.Admin];

    public async Task<ErrorOr<VendingCashUpSheetDto>> Handle(
        QueryVendingCashUpCommand command,
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

        var note = command.Note?.Trim();
        if (string.IsNullOrEmpty(note))
        {
            return Errors.VendingCashUp.QueryNoteRequired;
        }

        var cashUp = await VendingCashUpSheets.FindAsync(db, command.CashUpId, tracking: true, cancellationToken);
        if (cashUp is null)
        {
            return Errors.VendingCashUp.NotFound;
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

        cashUp.Status = VendingCashUpStatuses.Queried;
        cashUp.QueryNote = note;
        cashUp.QueriedByUserId = actor.Id;
        cashUp.QueriedByName = actor.Username;
        cashUp.QueriedAt = DateTime.UtcNow;
        cashUp.Revision += 1;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Vending cash-up {CashUpId} for {Cashier} on {Date:yyyy-MM-dd} queried by {Actor}",
            cashUp.Id, cashUp.CashierUsername, cashUp.BusinessDate, actor.Username);

        try
        {
            await auditService.LogAsync(
                "VendingCashUpQueried",
                "VendingCashUp",
                cashUp.Id.ToString(),
                $"Queried the cash-up of {cashUp.CashierUsername} at {cashUp.WarehouseCode} for {cashUp.BusinessDate:dd MMM yyyy}: {note}",
                true);
        }
        catch
        {
        }

        return await VendingCashUpSheets.BuildAsync(
            db, cashUp.CashierUserId, cashUp.WarehouseCode, cashUp.BusinessDate, cashUp, actor.Id, cancellationToken);
    }
}
