using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;
using static ShopInventory.Features.TradingCutoff.TradingCutoffKeys;

namespace ShopInventory.Features.TradingCutoff;

/// <summary>
/// Asked by every handler that takes a new transaction from a till, a vending cart or a van: is this
/// account still trading?
/// </summary>
/// <remarks>
/// <para>
/// <b>Where callers ask it.</b> After a resend of an earlier transaction has been recognised, and before
/// anything is reserved, signed or posted. A sale rung up at 16:59 whose answer was lost and which is sent
/// again at 17:01 must still be handed its sale; a new one at 17:01 must be refused with nothing done.
/// </para>
/// <para>
/// <b>What it never refuses.</b> A sale that already carries a fiscal receipt — the offline van upload and
/// a handset-stamped sale. The receipt exists at ZIMRA whatever this says, and refusing it would strand it.
/// </para>
/// <para>
/// Two rows read per transaction, by key. Not cached, so a change in Settings takes effect on the next sale
/// on every node.
/// </para>
/// </remarks>
public sealed class TradingCutoffGate(
    ApplicationDbContext db,
    TimeProvider clock,
    ILogger<TradingCutoffGate> logger)
{
    public const string Sale = "sale";
    public const string Order = "order";

    /// <param name="channel">One of <see cref="TradingCutoffKeys.Channels"/>.</param>
    /// <param name="userId">The signed-in account.</param>
    /// <param name="transaction"><see cref="Sale"/> or <see cref="Order"/>, for the refusal's wording.</param>
    /// <param name="reference">The transaction's own reference, for the log.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <param name="isResend">
    /// Asked only once trading is closed: whether this request is a resend of one the system already holds,
    /// which is let through to be answered as before. See <see cref="TradingCutoffResends"/>. Null for a
    /// caller that has already answered its resends before asking.
    /// </param>
    public async Task<ErrorOr<Success>> CheckAsync(
        string channel,
        Guid userId,
        string transaction,
        string? reference,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? isResend = null)
    {
        var channelKey = ChannelKey(channel);
        var accountKey = AccountKey(userId);

        var rows = await db.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == channelKey || config.Key == accountKey)
            .Select(config => new { config.Key, config.Value })
            .ToListAsync(cancellationToken);

        var channelSetting = ParseChannel(rows.FirstOrDefault(row => row.Key == channelKey)?.Value);
        var accountSetting = ParseAccount(rows.FirstOrDefault(row => row.Key == accountKey)?.Value);

        var nowCat = AuditService.ToCAT(clock.GetUtcNow().UtcDateTime);
        var decision = TradingCutoffRule.Evaluate(channelSetting, accountSetting, nowCat);

        if (!decision.IsClosed)
        {
            return Result.Success;
        }

        if (isResend is not null && await isResend(cancellationToken))
        {
            logger.LogInformation(
                "Let {Channel} {Transaction} {Reference} from account {UserId} past the {ClosedAt} CAT cut-off: it is a resend of one already held",
                channel,
                transaction,
                reference,
                userId,
                Format(decision.ClosedAt!.Value));
            return Result.Success;
        }

        logger.LogWarning(
            "Refused {Channel} {Transaction} {Reference} from account {UserId}: trading closed at {ClosedAt} CAT ({By})",
            channel,
            transaction,
            reference ?? "(no reference)",
            userId,
            Format(decision.ClosedAt!.Value),
            decision.ByAccount ? "account exception" : "channel cut-off");

        return Common.Errors.Errors.TradingCutoff.Closed(channel, decision.ClosedAt.Value, decision.ByAccount, transaction);
    }
}
