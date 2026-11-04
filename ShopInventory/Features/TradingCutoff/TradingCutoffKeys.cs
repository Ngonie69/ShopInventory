using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.TradingCutoff;

/// <summary>
/// Where the cut-off lives in <c>SystemConfigs</c>, and how it is read and written.
/// </summary>
/// <remarks>
/// <para>
/// <c>SystemConfigs</c> for the reason the posting-date switch is there: an admin changes it from Web →
/// Settings and the next sale on every node honours it, with no deploy. One row per channel and one row
/// per account exception, rather than one JSON blob, because the column holds 4000 characters and the
/// account list has no fixed size.
/// </para>
/// <para>
/// The channels are <see cref="SaleBusinesses"/>' keys, so the cut-off and the sales reports name the
/// same three businesses the same way.
/// </para>
/// </remarks>
public static class TradingCutoffKeys
{
    public const string Category = "TradingCutoff";
    public const string ChangedBy = "TradingCutoff.ChangedBy";
    public const string AccountKeyPrefix = "TradingCutoff.Account.";
    private const string ChannelKeyPrefix = "TradingCutoff.Channel.";

    /// <summary>The channels, in the order Settings shows them.</summary>
    public static readonly string[] Channels = SaleBusinesses.All;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ChannelKey(string channel) => ChannelKeyPrefix + channel;

    public static string AccountKey(Guid userId) => AccountKeyPrefix + userId.ToString("D");

    /// <summary>"HH:mm", the one form the cut-off's times are written and read in.</summary>
    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value?.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>
    /// The channel a sign-in account trades in, read from its roles, or null for an account that does not
    /// sell. Used to group accounts on the Settings page; a transaction is judged by the channel it
    /// arrived through, not by this.
    /// </summary>
    public static string? ChannelOf(IReadOnlyCollection<string> roles)
    {
        bool Has(string role) => roles.Contains(role, StringComparer.OrdinalIgnoreCase);

        if (Has(ApplicationRoles.TillOperator)) return SaleBusinesses.Shops;
        if (Has(ApplicationRoles.CartVendor)) return SaleBusinesses.Vending;
        if (Has(ApplicationRoles.Sales) || Has(ApplicationRoles.Adr)) return SaleBusinesses.Vans;
        return null;
    }

    /// <summary>
    /// A stored channel row. A row that cannot be read trades all day: a broken setting must not shut
    /// the counters.
    /// </summary>
    public static TradingCutoffChannelSetting ParseChannel(string? value)
    {
        var stored = Deserialize<StoredChannel>(value);
        return stored is not null && TryParseTime(stored.CutoffTime, out var time)
            ? new TradingCutoffChannelSetting(stored.Enabled, time)
            : TradingCutoffChannelSetting.Default;
    }

    /// <summary>A stored account row, or null — follow the channel — when it cannot be read.</summary>
    public static TradingCutoffAccountSetting? ParseAccount(string? value)
    {
        var stored = Deserialize<StoredAccount>(value);
        if (stored is null || stored.Mode == TradingCutoffAccountMode.FollowChannel)
        {
            return null;
        }

        return new TradingCutoffAccountSetting(
            stored.Mode,
            TryParseTime(stored.CutoffTime, out var time) ? time : null);
    }

    public static string SerializeChannel(TradingCutoffChannelSetting setting) =>
        JsonSerializer.Serialize(new StoredChannel(setting.Enabled, Format(setting.CutoffTime)), Json);

    public static string SerializeAccount(TradingCutoffAccountSetting setting) =>
        JsonSerializer.Serialize(
            new StoredAccount(setting.Mode, setting.CutoffTime is { } time ? Format(time) : null), Json);

    /// <summary>Stages a value; the caller saves.</summary>
    public static async Task StageAsync(
        ApplicationDbContext db,
        string key,
        string valueType,
        string? value,
        string description,
        CancellationToken cancellationToken)
    {
        var row = await db.SystemConfigs.FirstOrDefaultAsync(config => config.Key == key, cancellationToken);
        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = key,
                ValueType = valueType,
                Category = Category,
                Description = description,
                IsEditable = true
            };
            db.SystemConfigs.Add(row);
        }

        row.Value = value;
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static T? Deserialize<T>(string? value) where T : class
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredChannel(bool Enabled, string? CutoffTime);

    private sealed record StoredAccount(TradingCutoffAccountMode Mode, string? CutoffTime);
}
