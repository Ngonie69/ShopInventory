using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;
using static ShopInventory.Features.TradingCutoff.TradingCutoffKeys;

namespace ShopInventory.Features.TradingCutoff.Queries.GetTradingCutoffSettings;

public sealed class GetTradingCutoffSettingsHandler(ApplicationDbContext db, TimeProvider clock)
    : IRequestHandler<GetTradingCutoffSettingsQuery, ErrorOr<TradingCutoffSettings>>
{
    public async Task<ErrorOr<TradingCutoffSettings>> Handle(
        GetTradingCutoffSettingsQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key.StartsWith("TradingCutoff."))
            .Select(config => new { config.Key, config.Value, config.UpdatedAt })
            .ToListAsync(cancellationToken);

        var nowCat = AuditService.ToCAT(clock.GetUtcNow().UtcDateTime);

        var channels = Channels
            .ToDictionary(channel => channel, channel => ParseChannel(rows.FirstOrDefault(row => row.Key == ChannelKey(channel))?.Value));

        var exceptions = rows
            .Where(row => row.Key.StartsWith(AccountKeyPrefix, StringComparison.Ordinal))
            .Select(row => (Id: Guid.TryParse(row.Key[AccountKeyPrefix.Length..], out var id) ? id : Guid.Empty, Setting: ParseAccount(row.Value)))
            .Where(entry => entry.Id != Guid.Empty && entry.Setting is not null)
            .ToDictionary(entry => entry.Id, entry => entry.Setting!);

        // Every account is read and filtered by role here rather than in SQL: roles live in a main column
        // and a JSON column, and the table holds a few hundred rows.
        var users = await db.Users
            .AsNoTracking()
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.FirstName,
                user.LastName,
                user.Role,
                user.AdditionalRoles,
                user.IsActive,
                user.AssignedBusinessPartnerCode,
                ShopName = user.Shop != null ? user.Shop.Name : null
            })
            .ToListAsync(cancellationToken);

        var accounts = users
            .Select(user =>
            {
                var roles = new List<string> { user.Role };
                roles.AddRange(Models.User.ParseAdditionalRoles(user.AdditionalRoles));
                var channel = ChannelOf(roles);
                exceptions.TryGetValue(user.Id, out var exception);
                return (User: user, Channel: channel, Exception: exception);
            })
            .Where(entry => entry.Channel is not null || entry.Exception is not null)
            .Select(entry =>
            {
                var channelSetting = channels.GetValueOrDefault(entry.Channel ?? string.Empty) ?? TradingCutoffChannelSetting.Default;
                var name = string.Join(' ', new[] { entry.User.FirstName, entry.User.LastName }
                    .Where(part => !string.IsNullOrWhiteSpace(part))).Trim();

                return new TradingCutoffAccountView(
                    entry.User.Id,
                    entry.User.Username,
                    string.IsNullOrWhiteSpace(name) ? null : name,
                    entry.User.Role,
                    entry.Channel,
                    entry.User.ShopName ?? entry.User.AssignedBusinessPartnerCode,
                    entry.User.IsActive,
                    (entry.Exception?.Mode ?? TradingCutoffAccountMode.FollowChannel).ToString(),
                    entry.Exception?.CutoffTime is { } own ? Format(own) : null,
                    TradingCutoffRule.Evaluate(channelSetting, entry.Exception, nowCat).IsClosed);
            })
            .OrderBy(account => ChannelOrder(account.Channel))
            .ThenByDescending(account => account.IsActive)
            .ThenBy(account => account.Detail ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var channelViews = Channels
            .Select(channel => new TradingCutoffChannelView(
                channel,
                SaleBusinesses.Label(channel),
                channels[channel].Enabled,
                Format(channels[channel].CutoffTime),
                TradingCutoffRule.Evaluate(channels[channel], null, nowCat).IsClosed))
            .ToList();

        var changedBy = rows.FirstOrDefault(row => row.Key == ChangedBy);

        return new TradingCutoffSettings(
            channelViews,
            accounts,
            nowCat,
            changedBy?.UpdatedAt,
            changedBy?.Value);
    }

    /// <summary>Shops, vending, vans, then accounts that sell in none of them.</summary>
    private static int ChannelOrder(string? channel)
    {
        var index = Array.IndexOf(Channels, channel ?? string.Empty);
        return index < 0 ? int.MaxValue : index;
    }
}
