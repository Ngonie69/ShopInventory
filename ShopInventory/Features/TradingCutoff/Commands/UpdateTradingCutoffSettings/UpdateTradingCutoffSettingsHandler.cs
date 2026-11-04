using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.TradingCutoff.Queries.GetTradingCutoffSettings;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.TradingCutoff.TradingCutoffKeys;

namespace ShopInventory.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;

public sealed class UpdateTradingCutoffSettingsHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IAuditService auditService,
    ILogger<UpdateTradingCutoffSettingsHandler> logger)
    : IRequestHandler<UpdateTradingCutoffSettingsCommand, ErrorOr<TradingCutoffSettings>>
{
    public async Task<ErrorOr<TradingCutoffSettings>> Handle(
        UpdateTradingCutoffSettingsCommand request, CancellationToken cancellationToken)
    {
        var who = string.IsNullOrWhiteSpace(request.CallerName) ? request.CallerUserId.ToString() : request.CallerName;

        // FollowChannel is the absence of an exception, so it is stored as no row.
        var exceptions = request.Accounts
            .Select(account => (
                account.UserId,
                Setting: new TradingCutoffAccountSetting(
                    Enum.Parse<TradingCutoffAccountMode>(account.Mode, ignoreCase: true),
                    TryParseTime(account.CutoffTime, out var own) ? own : null)))
            .Where(entry => entry.Setting.Mode != TradingCutoffAccountMode.FollowChannel)
            .ToList();

        var exceptionIds = exceptions.Select(entry => entry.UserId).ToList();
        var known = await db.Users
            .AsNoTracking()
            .Where(user => exceptionIds.Contains(user.Id))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        var missing = exceptionIds.Except(known).ToList();
        if (missing.Count > 0)
        {
            return Common.Errors.Errors.TradingCutoff.AccountsNotFound(missing);
        }

        foreach (var channel in request.Channels)
        {
            var key = channel.Channel.Trim().ToLowerInvariant();
            TryParseTime(channel.CutoffTime, out var time);

            await StageAsync(db, ChannelKey(key), "json",
                SerializeChannel(new TradingCutoffChannelSetting(channel.Enabled, time)),
                $"Whether {SaleBusinesses.Label(key)} stop taking transactions at a time of day (CAT), and when.",
                cancellationToken);
        }

        // The list is the complete set of exceptions: anything stored and not sent goes back to its channel.
        var keep = exceptions.Select(entry => AccountKey(entry.UserId)).ToHashSet(StringComparer.Ordinal);
        var stale = await db.SystemConfigs
            .Where(config => config.Key.StartsWith(AccountKeyPrefix))
            .ToListAsync(cancellationToken);
        db.SystemConfigs.RemoveRange(stale.Where(config => !keep.Contains(config.Key)));

        foreach (var (userId, setting) in exceptions)
        {
            await StageAsync(db, AccountKey(userId), "json", SerializeAccount(setting),
                "One account's exception to its channel's trading cut-off.", cancellationToken);
        }

        await StageAsync(db, ChangedBy, "string", who,
            "The admin who last saved the trading cut-off.", cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var summary = Describe(request.Channels, exceptions.Count);
        logger.LogInformation("Trading cut-off saved by {User}: {Summary}", who, summary);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateTradingCutoff,
                "TradingCutoff",
                null,
                $"Trading cut-off saved by {who}: {summary}",
                true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not audit the trading cut-off change");
        }

        return await mediator.Send(new GetTradingCutoffSettingsQuery(), cancellationToken);
    }

    /// <summary>"Shops at 17:00, Vending off, Van sales at 17:00; 2 account exceptions".</summary>
    internal static string Describe(IEnumerable<TradingCutoffChannelInput> channels, int exceptionCount)
    {
        var parts = channels.Select(channel =>
        {
            var label = SaleBusinesses.Label(channel.Channel.Trim().ToLowerInvariant());
            return channel.Enabled ? $"{label} at {channel.CutoffTime.Trim()}" : $"{label} off";
        });

        return $"{string.Join(", ", parts)}; {exceptionCount} account exception{(exceptionCount == 1 ? "" : "s")}";
    }
}
