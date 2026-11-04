using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Features.TradingCutoff;
using ShopInventory.Web.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;
using ShopInventory.Web.Features.TradingCutoff.Queries.GetTradingCutoffSettings;

namespace ShopInventory.Web.Components;

public partial class TradingCutoffCard
{
    private const string AllFilter = "all";
    private const string ExceptionsFilter = "exceptions";

    private static readonly NocturneSelectOption<string>[] ModeOptions =
    [
        new(TradingCutoffModes.FollowChannel, "Follows channel", "neutral") { IsUnset = true },
        new(TradingCutoffModes.Exempt, "Never cut off", "good"),
        new(TradingCutoffModes.CutOff, "Own cut-off", "warn")
    ];

    private static readonly (string Key, string Label)[] Filters =
    [
        (AllFilter, "All"),
        ("shops", "Shops"),
        ("vending", "Vending"),
        ("vans", "Vans"),
        (ExceptionsFilter, "Exceptions")
    ];

    [Inject] private IMediator Mediator { get; set; } = null!;

    private TradingCutoffSettings? settings;
    private List<ChannelDraft> channels = [];
    private List<AccountDraft> accounts = [];
    private bool isLoading = true;
    private bool isSaving;
    private string? message;
    private bool messageSuccess;
    private string filter = AllFilter;
    private string search = string.Empty;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        isLoading = true;
        var result = await Mediator.Send(new GetTradingCutoffSettingsQuery());
        isLoading = false;

        if (result.IsError)
        {
            settings = null;
            message = result.FirstError.Description;
            messageSuccess = false;
            return;
        }

        Apply(result.Value);
    }

    private void Apply(TradingCutoffSettings value)
    {
        settings = value;
        Reset();
    }

    /// <summary>Puts the drafts back to what was last saved.</summary>
    private void Reset()
    {
        if (settings is null)
        {
            return;
        }

        channels = settings.Channels.Select(c => new ChannelDraft(c.Channel, c.Label) { Enabled = c.Enabled, CutoffTime = c.CutoffTime }).ToList();
        accounts = settings.Accounts.Select(a => new AccountDraft(a) { Mode = a.Mode, CutoffTime = a.CutoffTime }).ToList();
    }

    private async Task SaveAsync()
    {
        if (!IsDirty || HasErrors)
        {
            return;
        }

        isSaving = true;
        message = null;

        try
        {
            var result = await Mediator.Send(new UpdateTradingCutoffSettingsCommand(
                channels.Select(c => new TradingCutoffChannelInput(c.Channel, c.Enabled, Normalize(c.CutoffTime)!)).ToList(),
                accounts.Select(a => new TradingCutoffAccountInput(
                    a.UserId,
                    a.Mode,
                    a.Mode == TradingCutoffModes.CutOff ? Normalize(a.CutoffTime) : null)).ToList()));

            if (result.IsError)
            {
                message = result.FirstError.Description;
                messageSuccess = false;
                return;
            }

            Apply(result.Value);
            message = Summary(result.Value);
            messageSuccess = true;
        }
        finally
        {
            isSaving = false;
        }
    }

    private bool IsDirty =>
        settings is not null
        && (channels.Any(c => settings.Channels.FirstOrDefault(s => s.Channel == c.Channel) is not { } s
                || s.Enabled != c.Enabled
                || Normalize(s.CutoffTime) != Normalize(c.CutoffTime))
            || accounts.Any(a => settings.Accounts.FirstOrDefault(s => s.UserId == a.UserId) is not { } s
                || s.Mode != a.Mode
                || (a.Mode == TradingCutoffModes.CutOff && Normalize(s.CutoffTime) != Normalize(a.CutoffTime))));

    private bool HasErrors =>
        channels.Any(c => TimeError(c.CutoffTime) is not null)
        || accounts.Any(a => a.Mode == TradingCutoffModes.CutOff && TimeError(a.CutoffTime, allowBlank: true) is not null);

    private List<AccountDraft> VisibleAccounts
    {
        get
        {
            var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return accounts
                .Where(a => Matches(a, filter))
                .Where(a => terms.All(term =>
                    a.Username.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (a.DisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (a.Detail?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();
        }
    }

    private int CountFor(string key) => accounts.Count(a => Matches(a, key));

    private static bool Matches(AccountDraft account, string key) => key switch
    {
        AllFilter => true,
        ExceptionsFilter => account.Mode != TradingCutoffModes.FollowChannel,
        _ => account.Channel == key
    };

    private string ChannelLabel(string? channel) =>
        channels.FirstOrDefault(c => c.Channel == channel)?.Label ?? "No selling role";

    private string ChannelTime(string? channel) =>
        Normalize(channels.FirstOrDefault(c => c.Channel == channel)?.CutoffTime) ?? "17:00";

    private static string ChannelDescription(string channel) => channel switch
    {
        "shops" => "Shop tills. A sale rung up after the cut-off is refused at the counter with the reason.",
        "vending" => "Vending carts. A vendor invoice after the cut-off is refused.",
        "vans" => "Van handsets: sales, orders and order conversions. A resend of one started before the cut-off still goes through.",
        _ => string.Empty
    };

    /// <summary>What is wrong with a typed time, or null when it is a cut-off the API will take.</summary>
    private static string? TimeError(string? value, bool allowBlank = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return allowBlank ? null : "Enter a time, like 17:00.";
        }

        return Normalize(value) is null ? $"'{value}' is not a time. Use 24-hour HH:mm, after 00:00, like 17:00." : null;
    }

    /// <summary>"HH:mm" for a valid time after midnight; null otherwise.</summary>
    private static string? Normalize(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        && time > TimeOnly.MinValue
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;

    private static string Summary(TradingCutoffSettings saved)
    {
        var on = saved.Channels.Where(c => c.Enabled).Select(c => $"{c.Label} at {c.CutoffTime}").ToList();
        var exceptions = saved.Accounts.Count(a => a.Mode != TradingCutoffModes.FollowChannel);
        var channelsText = on.Count == 0 ? "No channel is cut off" : $"Cut off: {string.Join(", ", on)}";
        return $"Saved. {channelsText}; {exceptions} account exception{(exceptions == 1 ? "" : "s")}.";
    }

    private sealed class ChannelDraft(string channel, string label)
    {
        public string Channel { get; } = channel;
        public string Label { get; } = label;
        public bool Enabled { get; set; }
        public string CutoffTime { get; set; } = "17:00";
    }

    private sealed class AccountDraft(TradingCutoffAccount account)
    {
        public Guid UserId { get; } = account.UserId;
        public string Username { get; } = account.Username;
        public string? DisplayName { get; } = account.DisplayName;
        public string? Channel { get; } = account.Channel;
        public string? Detail { get; } = account.Detail;
        public bool IsActive { get; } = account.IsActive;
        public string Mode { get; set; } = TradingCutoffModes.FollowChannel;
        public string? CutoffTime { get; set; }
    }
}
