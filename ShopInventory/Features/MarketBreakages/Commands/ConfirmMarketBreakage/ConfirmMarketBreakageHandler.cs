using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Validation;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.MarketBreakages.Commands.ConfirmMarketBreakage;

/// <summary>
/// Records the office's count and transfers it from the van to the returns warehouse in SAP.
/// </summary>
/// <remarks>
/// <para>
/// Confirming again is how a failed or stranded transfer is retried, so the same command serves
/// both, and the office may correct a count on the retry.
/// </para>
/// <para>
/// Two guards, each covering what the other cannot. The post lock (<see cref="IIdempotencyRequestStore"/>,
/// keyed on the report alone, never on the caller) stops two people confirming the same report from
/// putting two transfers into SAP. The <see cref="MarketBreakageStatuses.Transferring"/> claim, taken
/// in one conditional statement, stops a reject landing on a report whose stock is moving.
/// </para>
/// <para>
/// Once the claim is taken the request's token is dropped: from there the transfer is an obligation,
/// and a closed browser tab must not leave the report stranded half way.
/// </para>
/// </remarks>
public sealed class ConfirmMarketBreakageHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IStockValidationService stockValidation,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> sapSettings,
    IOptions<MarketBreakageSettings> breakageSettings,
    ILogger<ConfirmMarketBreakageHandler> logger)
    : IRequestHandler<ConfirmMarketBreakageCommand, ErrorOr<MarketBreakageDecisionResultDto>>
{
    public const string IdempotencyScope = "market-breakage-transfer";

    /// <summary>SAP's Comments column on a stock transfer.</summary>
    private const int SapCommentsLength = 254;

    public async Task<ErrorOr<MarketBreakageDecisionResultDto>> Handle(
        ConfirmMarketBreakageCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.MarketBreakage.SapDisabled;

        var current = await ReadDetailAsync(command.BreakageId, cancellationToken);
        if (current is null)
            return Errors.MarketBreakage.NotFound(command.BreakageId);

        if (current.Status == MarketBreakageStatuses.Transferred)
            return AlreadyTransferred(current);

        if (!MarketBreakageStatuses.MayConfirm(current.Status))
            return Errors.MarketBreakage.NotActionable(current.Status);

        var linesCheck = CheckLines(current, command.Lines);
        if (linesCheck.IsError)
            return linesCheck.Errors;

        var userName = await MarketBreakageActor.ResolveNameAsync(context, command.UserId, cancellationToken);
        if (userName is null)
            return Errors.MarketBreakage.UserNotFound;

        var acquired = await idempotencyRequestStore.TryAcquireAsync<MarketBreakageTransferReceipt>(
            IdempotencyScope,
            command.BreakageId.ToString(),
            new { BreakageId = command.BreakageId },
            cancellationToken);

        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.ReplayAvailable:
                var replayed = await ReadDetailAsync(command.BreakageId, CancellationToken.None);
                return AlreadyTransferred(replayed ?? current);
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.MarketBreakage.PostInProgress;
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("market breakage transfer");
        }

        var requestId = acquired.RequestId!.Value;
        var release = true;
        try
        {
            var result = await ClaimAndTransferAsync(command, userName, cancellationToken);
            if (!result.IsError && result.Value.Breakage.SapDocEntry is int docEntry)
            {
                await idempotencyRequestStore.CompleteAsync(
                    requestId,
                    new MarketBreakageTransferReceipt(docEntry, result.Value.Breakage.SapDocNum),
                    CancellationToken.None);
                release = false;
            }

            return result;
        }
        finally
        {
            if (release)
            {
                try { await idempotencyRequestStore.ReleaseAsync(requestId, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed to release the transfer lock for market breakage {BreakageId}", command.BreakageId);
                }
            }
        }
    }

    private async Task<ErrorOr<MarketBreakageDecisionResultDto>> ClaimAndTransferAsync(
        ConfirmMarketBreakageCommand command,
        string userName,
        CancellationToken cancellationToken)
    {
        var returnsWarehouse = breakageSettings.Value.ReturnsWarehouseCode.Trim();
        var now = DateTime.UtcNow;
        var remarks = Blank(command.Remarks);
        var userId = command.UserId;

        var claimed = await context.MarketBreakages
            .Where(row => row.Id == command.BreakageId
                && (row.Status == MarketBreakageStatuses.Pending
                    || row.Status == MarketBreakageStatuses.TransferFailed
                    || row.Status == MarketBreakageStatuses.Transferring))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, MarketBreakageStatuses.Transferring)
                .SetProperty(row => row.DecidedByUserId, userId)
                .SetProperty(row => row.DecidedByName, userName)
                .SetProperty(row => row.DecidedAtUtc, now)
                .SetProperty(row => row.DecisionRemarks, remarks)
                .SetProperty(row => row.ReturnsWarehouseCode, returnsWarehouse)
                .SetProperty(row => row.LastAttemptedAtUtc, now),
                cancellationToken);

        if (claimed == 0)
        {
            var moved = await ReadDetailAsync(command.BreakageId, cancellationToken);
            return moved is null
                ? Errors.MarketBreakage.NotFound(command.BreakageId)
                : Errors.MarketBreakage.NotActionable(moved.Status);
        }

        // Past the claim: nothing downstream may be cancelled by the caller going away.
        var token = CancellationToken.None;

        // Loaded after the claim, so the tracked copy is the claimed row rather than a stale one
        // the conditional update wrote around.
        var breakage = await context.MarketBreakages
            .AsTracking()
            .Include(item => item.Lines)
            .SingleAsync(item => item.Id == command.BreakageId, token);

        var confirmedById = command.Lines.ToDictionary(line => line.LineId, line => line.ConfirmedQuantity);
        foreach (var line in breakage.Lines)
            line.ConfirmedQuantity = confirmedById[line.Id];

        await context.SaveChangesAsync(token);

        var transferLines = breakage.Lines
            .Where(line => line.ConfirmedQuantity > 0)
            .OrderBy(line => line.LineNum)
            .Select(line => new CreateInventoryTransferLineRequest
            {
                ItemCode = line.ItemCode,
                Quantity = line.ConfirmedQuantity!.Value,
                FromWarehouseCode = breakage.VanWarehouseCode,
                ToWarehouseCode = returnsWarehouse
            })
            .ToList();

        var payload = new CreateInventoryTransferRequest
        {
            FromWarehouse = breakage.VanWarehouseCode,
            ToWarehouse = returnsWarehouse,
            ClientRequestId = $"market-breakage-{breakage.Id}",
            Comments = BuildComments(breakage, userName),
            Lines = transferLines
        };

        var quantityErrors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            transferLines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            token);
        if (quantityErrors.Count > 0)
        {
            var message = string.Join("; ", quantityErrors);
            await FailAsync(breakage, message);
            return Errors.MarketBreakage.LinesMismatch(message);
        }

        try
        {
            var stock = await stockValidation.ValidateInventoryTransferStockAsync(payload, token);
            if (!stock.IsValid)
            {
                var message = $"Van {breakage.VanWarehouseCode} does not hold enough stock in SAP: "
                    + string.Join("; ", stock.Errors.Select(error => error.Message));
                await FailAsync(breakage, message);
                return Errors.MarketBreakage.InsufficientStock(message);
            }

            if (!stock.StockWasFullyRead)
            {
                var message = "Could not read stock from SAP for warehouse(s) "
                    + string.Join(", ", stock.UnreadableWarehouses)
                    + ". Nothing was transferred; confirm again once SAP is answering.";
                await FailAsync(breakage, message);
                return Errors.MarketBreakage.TransferFailed(message);
            }

            var transfer = await sapClient.CreateInventoryTransferAsync(payload, stock.PreFetchedData, token);

            breakage.Status = MarketBreakageStatuses.Transferred;
            breakage.SapDocEntry = transfer.DocEntry;
            breakage.SapDocNum = transfer.DocNum;
            breakage.TransferredAtUtc = DateTime.UtcNow;
            breakage.LastError = null;
            await context.SaveChangesAsync(token);

            logger.LogInformation(
                "Market breakage {BreakageId} transferred {From} -> {To}. DocEntry {DocEntry}, DocNum {DocNum}",
                breakage.Id, breakage.VanWarehouseCode, returnsWarehouse, transfer.DocEntry, transfer.DocNum);

            await AuditAsync(breakage, returnsWarehouse, transfer.DocNum);

            return new MarketBreakageDecisionResultDto
            {
                Message = $"Confirmed. Transfer #{transfer.DocNum} moved the stock from {breakage.VanWarehouseCode} to {returnsWarehouse}.",
                Breakage = (await ReadDetailAsync(breakage.Id, token))!
            };
        }
        catch (OperationCanceledException exception)
        {
            // Nothing cancels this token, so this is the SAP client's own timeout: the request reached
            // SAP and no answer came back. The transfer may exist, so the report says so rather than
            // inviting a retry that would move the stock twice.
            logger.LogError(exception,
                "The SAP transfer for market breakage {BreakageId} timed out; the outcome is unknown", breakage.Id);
            const string message = "SAP did not answer in time, so it is not known whether the transfer was created. "
                + "Check SAP for this transfer before confirming again — confirming again will post it again.";
            await FailAsync(breakage, message);
            return Errors.MarketBreakage.TransferFailed(message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to transfer market breakage {BreakageId} to SAP", breakage.Id);
            await FailAsync(breakage, exception.Message);
            return Errors.MarketBreakage.TransferFailed(
                $"The count was saved but SAP refused the transfer: {exception.Message}");
        }
    }

    private async Task FailAsync(MarketBreakageEntity breakage, string message)
    {
        breakage.Status = MarketBreakageStatuses.TransferFailed;
        breakage.LastError = message.Length > 2000 ? message[..2000] : message;
        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task AuditAsync(MarketBreakageEntity breakage, string returnsWarehouse, int docNum)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.ConfirmMarketBreakage, "MarketBreakage", breakage.Id.ToString(),
                $"Breakage report #{breakage.Id} from {breakage.ReportedByName} confirmed; transfer #{docNum} "
                + $"{breakage.VanWarehouseCode} -> {returnsWarehouse}",
                true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit the confirmation of market breakage {BreakageId}", breakage.Id);
        }
    }

    /// <summary>
    /// Every line must be counted, and exactly the lines this report has — a count sent for a report
    /// that has since changed on screen would otherwise transfer a line nobody looked at.
    /// </summary>
    private static ErrorOr<Success> CheckLines(
        MarketBreakageDetailDto current,
        IReadOnlyList<ConfirmMarketBreakageLineDto> lines)
    {
        var expected = current.Lines.Select(line => line.Id).ToHashSet();
        var given = lines.Select(line => line.LineId).ToHashSet();

        if (!expected.SetEquals(given))
            return Errors.MarketBreakage.LinesMismatch(
                "The confirmed lines do not match this report's lines. Reload the report and count it again.");

        if (lines.All(line => line.ConfirmedQuantity <= 0))
            return Errors.MarketBreakage.NothingToTransfer;

        return Result.Success;
    }

    private static string BuildComments(MarketBreakageEntity breakage, string confirmedBy)
    {
        var shop = string.IsNullOrWhiteSpace(breakage.CardName) ? breakage.CardCode : breakage.CardName;
        var text = $"Market breakage #{breakage.Id} from {breakage.ReportedByName}"
            + (string.IsNullOrWhiteSpace(shop) ? string.Empty : $", {shop}")
            + $". Confirmed by {confirmedBy}."
            + (string.IsNullOrWhiteSpace(breakage.DecisionRemarks) ? string.Empty : $" {breakage.DecisionRemarks}");
        return text.Length > SapCommentsLength ? text[..SapCommentsLength] : text;
    }

    private MarketBreakageDecisionResultDto AlreadyTransferred(MarketBreakageDetailDto breakage) => new()
    {
        Message = $"Breakage report #{breakage.Id} was already transferred to returns (transfer #{breakage.SapDocNum}).",
        Breakage = breakage
    };

    private Task<MarketBreakageDetailDto?> ReadDetailAsync(int id, CancellationToken cancellationToken)
        => context.MarketBreakages
            .AsNoTracking()
            .Where(breakage => breakage.Id == id)
            .Select(MarketBreakageProjections.Detail)
            .FirstOrDefaultAsync(cancellationToken);

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What a completed transfer lock remembers.</summary>
public sealed record MarketBreakageTransferReceipt(int DocEntry, int? DocNum);
