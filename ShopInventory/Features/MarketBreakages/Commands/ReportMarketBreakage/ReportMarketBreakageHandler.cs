using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.MarketBreakages.Commands.ReportMarketBreakage;

/// <summary>
/// Records what a rep collected. Moves nothing in SAP — that waits for the office's count.
/// </summary>
public sealed class ReportMarketBreakageHandler(
    ApplicationDbContext context,
    ILogger<ReportMarketBreakageHandler> logger)
    : IRequestHandler<ReportMarketBreakageCommand, ErrorOr<VanSalesMarketBreakageResponse>>
{
    public async Task<ErrorOr<VanSalesMarketBreakageResponse>> Handle(
        ReportMarketBreakageCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;
        var clientRequestId = request.ClientRequestId.Trim();

        // A handset that lost the reply resends the same id. It is told about the report it already
        // made rather than making a second one, which would ask the office to count the same crate twice.
        var existing = await FindExistingAsync(clientRequestId, cancellationToken);
        if (existing is not null)
            return Replay(existing, command.UserId);

        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
        if (user is null || !user.IsActive)
            return Errors.MarketBreakage.UserNotFound;

        var vanWarehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(vanWarehouseCode))
            return Errors.MarketBreakage.NoVanWarehouse;

        var reportedByName = string.Join(" ", new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

        var now = DateTime.UtcNow;
        var breakage = new MarketBreakageEntity
        {
            ClientRequestId = clientRequestId,
            ReportedByUserId = user.Id,
            ReportedByName = string.IsNullOrWhiteSpace(reportedByName) ? user.Username : reportedByName,
            VanWarehouseCode = vanWarehouseCode,
            CardCode = Blank(request.CardCode),
            CardName = Blank(request.CardName),
            Remarks = Blank(request.Remarks),
            CapturedAtUtc = NormaliseCapturedAt(request.CapturedAt, now),
            CreatedAtUtc = now,
            Status = MarketBreakageStatuses.Pending,
            Lines = request.Items
                .Select((item, index) => new MarketBreakageLineEntity
                {
                    LineNum = index,
                    ItemCode = item.Code.Trim().ToUpperInvariant(),
                    ItemDescription = Blank(item.Description),
                    Reason = Blank(item.Reason),
                    ReportedQuantity = item.Quantity
                })
                .ToList()
        };

        context.MarketBreakages.Add(breakage);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Two copies of the same submit raced past the lookup; the unique index let one through.
            context.Entry(breakage).State = EntityState.Detached;
            foreach (var line in breakage.Lines)
                context.Entry(line).State = EntityState.Detached;

            var winner = await FindExistingAsync(clientRequestId, cancellationToken);
            if (winner is null)
                throw;

            logger.LogInformation(exception,
                "Breakage report {ClientRequestId} was saved by a concurrent submit; replaying it", clientRequestId);
            return Replay(winner, command.UserId);
        }

        logger.LogInformation(
            "Market breakage {BreakageId} reported by {UserId} on {VanWarehouse}: {LineCount} line(s), {Quantity} unit(s)",
            breakage.Id, user.Id, vanWarehouseCode, breakage.Lines.Count, breakage.Lines.Sum(line => line.ReportedQuantity));

        return new VanSalesMarketBreakageResponse
        {
            Id = breakage.Id,
            Status = breakage.Status,
            AlreadyReported = false,
            Message = $"Breakage report #{breakage.Id} sent. The office will confirm it when the stock is off the van."
        };
    }

    private Task<MarketBreakageEntity?> FindExistingAsync(string clientRequestId, CancellationToken cancellationToken)
        => context.MarketBreakages
            .AsNoTracking()
            .FirstOrDefaultAsync(breakage => breakage.ClientRequestId == clientRequestId, cancellationToken);

    private static ErrorOr<VanSalesMarketBreakageResponse> Replay(MarketBreakageEntity existing, Guid userId)
    {
        if (existing.ReportedByUserId != userId)
            return Errors.MarketBreakage.DuplicateRequestFromAnotherUser;

        return new VanSalesMarketBreakageResponse
        {
            Id = existing.Id,
            Status = existing.Status,
            AlreadyReported = true,
            Message = $"Breakage report #{existing.Id} was already received."
        };
    }

    /// <summary>
    /// The handset's clock is trusted for when, but not for a time that has not happened yet or one
    /// so old it cannot be this report — both are a wrong clock, and the receipt time is closer.
    /// </summary>
    private static DateTime NormaliseCapturedAt(DateTime? capturedAt, DateTime now)
    {
        if (capturedAt is null)
            return now;

        var utc = capturedAt.Value.Kind switch
        {
            DateTimeKind.Utc => capturedAt.Value,
            DateTimeKind.Local => capturedAt.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(capturedAt.Value, DateTimeKind.Utc)
        };

        return utc > now.AddMinutes(10) || utc < now.AddDays(-60) ? now : utc;
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
