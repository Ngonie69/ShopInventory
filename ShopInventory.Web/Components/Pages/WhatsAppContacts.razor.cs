using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using ShopInventory.Web.Components.CustomerDocuments;
using ShopInventory.Web.Features.CustomerDocuments.Commands.OptOutCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Commands.RecheckCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Commands.RemoveCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Commands.UpdateCustomerWhatsAppContact;
using ShopInventory.Web.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactDetail;
using ShopInventory.Web.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactRegister;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Every WhatsApp number saved on a customer, in one place. The list is for finding and comparing
/// numbers; everything done to one number happens in its drawer, which also shows who first added it,
/// what was sent to it and its history.
/// </summary>
/// <remarks>
/// <para>
/// Numbers are not added here. Adding one belongs on the customer, where the shop's cards in its other
/// currencies are offered alongside.
/// </para>
/// <para>
/// The filters and the open number live in the address (<c>q</c>, <c>state</c>, <c>source</c>,
/// <c>contact</c>), so a search can be sent to someone and the delivery log can link straight to the
/// number a send went to — the drawer reads that number by id, so it opens whether or not the number
/// is on the list's current page.
/// </para>
/// </remarks>
public partial class WhatsAppContacts : ComponentBase, IDisposable
{
    private const int PageSize = 50;

    private static readonly NocturneSelectOption<string>[] StateOptions =
    [
        NocturneSelectOption.All("Linked"),
        new("auto", "Automatic", "good"),
        new("request", "On request", "info"),
        new("optedout", "Opted out", "warn"),
        new("notonwhatsapp", "Not on WhatsApp", "warn"),
        new("removed", "Removed")
    ];

    private static readonly NocturneSelectOption<string>[] SourceOptions =
    [
        NocturneSelectOption.All("Anywhere"),
        new("Web", SourceLabel("Web")),
        new("VanHandset", SourceLabel("VanHandset")),
        new("Till", SourceLabel("Till"))
    ];

    private readonly CancellationTokenSource disposal = new();

    private CustomerWhatsAppContactRegisterModel? register;
    private string? loadError;
    private bool isLoading = true;
    private DateTime? loadedAtUtc;

    private string? searchText;
    private string? stateFilter;
    private string? sourceFilter;
    private int pageNumber = 1;

    private int? openContactId;
    private CustomerWhatsAppContactDetailModel? detail;
    private string? detailError;
    private bool isBusy;
    private string? notice;
    private bool noticeIsError;
    private DrawerAction? confirming;

    private enum DrawerAction
    {
        OptOut,
        Remove
    }

    [Inject] private IMediator Mediator { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private bool HasFilters =>
        !string.IsNullOrWhiteSpace(searchText)
        || !string.IsNullOrEmpty(stateFilter)
        || !string.IsNullOrEmpty(sourceFilter);

    private int TotalPages => register is null || register.TotalCount == 0
        ? 1
        : (int)Math.Ceiling(register.TotalCount / (double)PageSize);

    private string StateTitle =>
        StateOptions.FirstOrDefault(option => option.Value == (stateFilter ?? string.Empty))?.Label ?? "Linked";

    /// <summary>The digits being searched for, as the API matches them, or null for a word search.</summary>
    private string? SearchDigits => PhoneSearchDigits(searchText);

    protected override async Task OnInitializedAsync()
    {
        var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(Navigation.Uri).Query);
        string? Read(string name) =>
            query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.ToString().Trim() : null;

        searchText = Read("q");
        stateFilter = StateOptions.Any(option => option.Value == Read("state")) ? Read("state") : null;
        sourceFilter = SourceOptions.Any(option => option.Value == Read("source")) ? Read("source") : null;

        var load = LoadAsync();
        if (int.TryParse(Read("contact"), out var contactId) && contactId > 0)
        {
            await Task.WhenAll(load, OpenAsync(contactId));
        }
        else
        {
            await load;
        }
    }

    private async Task LoadAsync()
    {
        isLoading = true;
        try
        {
            var result = await Mediator.Send(
                new GetCustomerWhatsAppContactRegisterQuery(
                    string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim(),
                    stateFilter,
                    sourceFilter,
                    null,
                    pageNumber,
                    PageSize),
                disposal.Token);

            if (result.IsError)
            {
                loadError = result.FirstError.Description;
                return;
            }

            loadError = null;
            register = result.Value;
            loadedAtUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task RefreshAsync()
    {
        await Task.WhenAll(LoadAsync(), openContactId is { } id ? LoadDetailAsync(id) : Task.CompletedTask);
    }

    private async Task ApplyFiltersAsync()
    {
        pageNumber = 1;
        WriteAddress();
        await LoadAsync();
    }

    private async Task ShowStateAsync(string state)
    {
        stateFilter = string.IsNullOrEmpty(state) ? null : state;
        await ApplyFiltersAsync();
    }

    private async Task ClearFiltersAsync()
    {
        searchText = null;
        stateFilter = null;
        sourceFilter = null;
        await ApplyFiltersAsync();
    }

    private async Task GoToPageAsync(int page)
    {
        pageNumber = Math.Clamp(page, 1, TotalPages);
        await LoadAsync();
    }

    /// <summary>Puts the filters and the open number in the address without a navigation.</summary>
    private void WriteAddress()
    {
        var uri = Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["q"] = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim(),
            ["state"] = stateFilter,
            ["source"] = sourceFilter,
            ["contact"] = openContactId
        });
        Navigation.NavigateTo(uri, replace: true);
    }

    private async Task OpenAsync(int contactId)
    {
        if (openContactId != contactId)
        {
            detail = null;
            notice = null;
            confirming = null;
        }

        openContactId = contactId;
        WriteAddress();
        await LoadDetailAsync(contactId);
    }

    private async Task LoadDetailAsync(int contactId)
    {
        detailError = null;
        try
        {
            var result = await Mediator.Send(new GetCustomerWhatsAppContactDetailQuery(contactId), disposal.Token);
            if (openContactId != contactId)
            {
                return;
            }

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
                detail = null;
                return;
            }

            detail = result.Value;
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
    }

    private void CloseDrawer()
    {
        if (isBusy)
        {
            return;
        }

        openContactId = null;
        detail = null;
        detailError = null;
        notice = null;
        confirming = null;
        WriteAddress();
    }

    private void OnDrawerKey(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            if (confirming is not null)
            {
                confirming = null;
            }
            else
            {
                CloseDrawer();
            }
        }
    }

    private async Task ToggleAutoSendAsync()
    {
        if (detail?.Contact is not { } contact)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await Mediator.Send(new UpdateCustomerWhatsAppContactCommand(
                contact.Id,
                new UpdateCustomerWhatsAppContactModel
                {
                    ContactName = contact.ContactName,
                    AutoSendInvoices = !contact.AutoSendInvoices
                }), disposal.Token);

            return result.IsError
                ? (result.FirstError.Description, true)
                : (result.Value.AutoSendInvoices
                    ? "New invoices will go here automatically."
                    : "Invoices go here only when someone sends one.", false);
        });
    }

    private async Task RecheckAsync()
    {
        if (detail?.Contact is not { } contact)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await Mediator.Send(new RecheckCustomerWhatsAppContactCommand(contact.Id), disposal.Token);
            return result.IsError
                ? (result.FirstError.Description, true)
                : (result.Value.WhatsAppExists == true
                    ? "WhatsApp says the number has an account."
                    : "WhatsApp has no account for this number.", result.Value.WhatsAppExists != true);
        });
    }

    private async Task ConfirmAsync()
    {
        if (detail?.Contact is not { } contact || confirming is not { } action)
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (action == DrawerAction.OptOut)
            {
                var optOut = await Mediator.Send(new OptOutCustomerWhatsAppContactCommand(contact.Id), disposal.Token);
                return optOut.IsError
                    ? (optOut.FirstError.Description, true)
                    : ("Opted out. Nothing more goes to this number, on any customer it is saved on.", false);
            }

            var removed = await Mediator.Send(new RemoveCustomerWhatsAppContactCommand(contact.Id), disposal.Token);
            return removed.IsError
                ? (removed.FirstError.Description, true)
                : ($"Taken off {contact.OwnerName}. What was sent stays on record.", false);
        });

        confirming = null;
    }

    /// <summary>Runs one change, then reads the number and the list again so both show what is now true.</summary>
    private async Task RunAsync(Func<Task<(string Message, bool IsError)>> action)
    {
        if (openContactId is not { } contactId)
        {
            return;
        }

        isBusy = true;
        notice = null;
        try
        {
            var (text, isError) = await action();
            notice = text;
            noticeIsError = isError;
            await Task.WhenAll(LoadDetailAsync(contactId), LoadAsync());
        }
        catch (OperationCanceledException) when (disposal.IsCancellationRequested)
        {
        }
        finally
        {
            isBusy = false;
        }
    }

    /// <summary>
    /// The digits a search looks for in a number, the way the API matches them: none for a search with
    /// letters or under three digits, and a local number's leading 0 dropped.
    /// </summary>
    internal static string? PhoneSearchDigits(string? search)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Any(char.IsLetter))
        {
            return null;
        }

        var digits = new string(search.Where(char.IsDigit).ToArray());
        if (digits.Length < 3)
        {
            return null;
        }

        return digits.Length >= 9 && digits[0] == '0' ? digits.TrimStart('0') : digits;
    }

    /// <summary>
    /// The number cut into runs, each marked as part of the searched-for digits or not. The last
    /// occurrence is marked, since a search by the end of a number is the usual one.
    /// </summary>
    internal static IReadOnlyList<(string Text, bool Hit)> PhoneSegments(string phone, string? digits)
    {
        if (string.IsNullOrEmpty(digits))
        {
            return [(phone, false)];
        }

        var all = new string(phone.Where(char.IsDigit).ToArray());
        var start = all.LastIndexOf(digits, StringComparison.Ordinal);
        if (start < 0)
        {
            return [(phone, false)];
        }

        var end = start + digits.Length;
        var segments = new List<(string Text, bool Hit)>();
        var digitIndex = 0;

        foreach (var ch in phone)
        {
            bool hit;
            if (char.IsDigit(ch))
            {
                hit = digitIndex >= start && digitIndex < end;
                digitIndex++;
            }
            else
            {
                hit = digitIndex > start && digitIndex < end;
            }

            if (segments.Count > 0 && segments[^1].Hit == hit)
            {
                segments[^1] = (segments[^1].Text + ch, hit);
            }
            else
            {
                segments.Add((ch.ToString(), hit));
            }
        }

        return segments;
    }

    private static string OwnerCode(CustomerWhatsAppContactModel contact, string? routeCustomerCode) =>
        contact.CardCode ?? (routeCustomerCode is null ? "Van route customer" : $"Route customer {routeCustomerCode}");

    private static string StateLabel(CustomerWhatsAppContactModel contact) =>
        contact.IsRemoved ? "Removed"
        : contact.IsOptedOut ? "Opted out"
        : contact.AutoSendInvoices ? "Automatic"
        : "On request";

    private static string StateTone(CustomerWhatsAppContactModel contact) =>
        contact.IsRemoved ? "neutral"
        : contact.IsOptedOut ? "warn"
        : contact.AutoSendInvoices ? "good"
        : "info";

    private static string CheckLine(CustomerWhatsAppContactModel contact) => contact.WhatsAppExists switch
    {
        null => "Never checked on WhatsApp",
        true => $"On WhatsApp · checked {CustomerDocumentDisplay.Stamp(contact.WhatsAppCheckedAtUtc)}",
        false => $"No WhatsApp account · checked {CustomerDocumentDisplay.Stamp(contact.WhatsAppCheckedAtUtc)}"
    };

    private static string EventWho(CustomerWhatsAppContactEventModel entry) => entry switch
    {
        { Via: "StopKeyword" } => "The customer",
        { Who: { } who, Via: { } via } => $"{who} · {SourceLabel(via)}",
        { Who: { } who } => who,
        { Kind: "Checked" } => "Who checked was not recorded",
        _ => "Who was not recorded"
    };

    private static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var letters = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => char.IsLetter(part[0]))
            .Select(part => char.ToUpperInvariant(part[0]))
            .Take(2)
            .ToArray();

        return letters.Length == 0 ? "?" : new string(letters);
    }

    private static string SourceLabel(string? source) => source switch
    {
        "Web" => "Web app",
        "VanHandset" => "Van handset",
        "Till" => "Till",
        _ => source ?? "—"
    };

    public void Dispose()
    {
        disposal.Cancel();
        disposal.Dispose();
    }
}
