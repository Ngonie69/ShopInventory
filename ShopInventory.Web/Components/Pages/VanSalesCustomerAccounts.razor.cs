using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.DeactivateVanSalesCustomerAccount;
using ShopInventory.Web.Features.VanSalesCustomerAccounts.Commands.OnboardVanSalesCustomerAccount;
using ShopInventory.Web.Features.VanSalesCustomerAccounts.Queries.GetVanSalesCustomerAccounts;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// Giving a shop access to the ordering app, and taking it away.
/// </summary>
/// <remarks>
/// This screen replaces onboarding a customer over WhatsApp by hand. The API endpoints it drives
/// have existed since the ordering app shipped; until now nothing in the back office called them,
/// so a rep who wanted a shop on the app had to ask someone to do it out of band.
/// <para>
/// Behaviour lives here rather than in the markup, per the repo's Blazor rules. What it decides is
/// what to load, what the form sends, and what to say when the API refuses.
/// </para>
/// </remarks>
public partial class VanSalesCustomerAccounts : ComponentBase, IDisposable
{
    [Inject]
    private IMediator Mediator { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    private readonly CancellationTokenSource disposeCts = new();

    private VanSalesCustomerAccountsViewModel view = new();

    private OnboardVanSalesCustomerAccountModel form = new();

    private string searchTerm = string.Empty;
    private int? routeCustomerFilter;

    /// <summary>Which sign-ins the list is showing.</summary>
    private enum AccountView
    {
        All,
        Active,
        Withdrawn
    }

    /// <summary>
    /// The window control, in the design's order: the two narrow answers first, the whole list
    /// last.
    /// </summary>
    private static readonly (AccountView Value, string Label)[] ViewFilters =
    [
        (AccountView.Active, "Active"),
        (AccountView.Withdrawn, "Withdrawn"),
        (AccountView.All, "All")
    ];

    /// <summary>
    /// The list opens on every sign-in, withdrawn ones included.
    /// </summary>
    /// <remarks>
    /// The question this screen is opened with is usually "why can this shop not order?", and a
    /// withdrawn account hidden behind a filter looks identical to one that was never created —
    /// which sends the operator to create a second one rather than reinstate the first.
    /// <para>
    /// The design rests its control on Active instead. Narrowing is a click away either way; what
    /// is not recoverable is the operator who never learns the row exists, so the wider window is
    /// the one to open on.
    /// </para>
    /// </remarks>
    private AccountView viewFilter = AccountView.All;

    /// <summary>
    /// Only the Active window narrows the query. Withdrawn and All read the same rows, and differ
    /// by which of them this page then shows.
    /// </summary>
    private bool IncludeInactive => viewFilter != AccountView.Active;

    private bool isLoading = true;
    private bool isSaving;

    private VanSalesCustomerAccountModel? accountPendingWithdrawal;
    private bool isWithdrawing;
    private string? withdrawErrorMessage;

    private string? formErrorMessage;

    /// <summary>The shops a sign-in can be given to. The code rides as the row's hint.</summary>
    private IEnumerable<NocturneSelectOption<int>> ShopOptions =>
        view.RouteCustomers.Select(customer =>
            new NocturneSelectOption<int>(customer.Id, customer.Name) { Hint = customer.Code });

    /// <summary>The same shops as a filter, behind an "All shops" row that clears it.</summary>
    private IEnumerable<NocturneSelectOption<int?>> ShopFilterOptions =>
        view.RouteCustomers
            .Select(customer =>
                new NocturneSelectOption<int?>(customer.Id, customer.Name) { Hint = customer.Code })
            .Prepend(new NocturneSelectOption<int?>(null, "All shops", "neutral")
            {
                RuleAfter = true
            });

    private IEnumerable<VanSalesCustomerAccountModel> FilteredAccounts =>
        view.Accounts.Where(account => MatchesView(account) && MatchesSearch(account));

    private bool MatchesView(VanSalesCustomerAccountModel account) => viewFilter switch
    {
        AccountView.Active => account.IsActive,
        AccountView.Withdrawn => !account.IsActive,
        _ => true
    };

    private bool MatchesSearch(VanSalesCustomerAccountModel account) =>
        string.IsNullOrWhiteSpace(searchTerm) ||
        account.RouteCustomerName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
        account.RouteCustomerCode.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
        account.PhoneE164.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
        (account.DisplayName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);

    private int ActiveCount => view.Accounts.Count(a => a.IsActive);

    protected override Task OnInitializedAsync() => LoadAsync();

    public void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
    }

    /// <summary>
    /// Moves the list to another window, reloading only when the new one needs rows the last query
    /// did not ask for.
    /// </summary>
    private async Task SetViewAsync(AccountView value)
    {
        if (viewFilter == value)
        {
            return;
        }

        var wasIncludingInactive = IncludeInactive;
        viewFilter = value;

        if (IncludeInactive != wasIncludingInactive)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (disposeCts.IsCancellationRequested)
        {
            return;
        }

        isLoading = true;

        try
        {
            var result = await Mediator.Send(
                new GetVanSalesCustomerAccountsQuery(routeCustomerFilter, IncludeInactive),
                disposeCts.Token);

            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            view = result.Value;
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task GiveAccessAsync()
    {
        if (isSaving)
        {
            return;
        }

        formErrorMessage = null;

        if (form.RouteCustomerId <= 0)
        {
            formErrorMessage = "Choose the shop this sign-in belongs to.";
            return;
        }

        if (string.IsNullOrWhiteSpace(form.PhoneNumber))
        {
            formErrorMessage = "Enter the number of the handset the shop will order from.";
            return;
        }

        isSaving = true;

        try
        {
            var result = await Mediator.Send(
                new OnboardVanSalesCustomerAccountCommand(
                    form.RouteCustomerId,
                    form.PhoneNumber,
                    form.DisplayName),
                disposeCts.Token);

            if (result.IsError)
            {
                formErrorMessage = result.FirstError.Description;
                return;
            }

            var account = result.Value;

            // Said in terms of what happens next rather than what was written to a table: the shop
            // still has to open the app and ask for a code, and an operator who thinks the job is
            // finished here will not tell them to.
            Snackbar.Add(
                $"{account.RouteCustomerName} can now sign in on {account.PhoneE164}. "
                + "They will get a code on WhatsApp when they open the app.",
                Severity.Success);

            form = new OnboardVanSalesCustomerAccountModel();
            await LoadAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    /// <summary>
    /// Reinstates a withdrawn sign-in by onboarding it again.
    /// </summary>
    /// <remarks>
    /// There is no reactivate endpoint, and this is not a workaround for the want of one: onboarding
    /// a phone that already has an account is defined to reinstate it, clear its lockout and keep
    /// its history, which is exactly what reinstating means here. The route customer and phone come
    /// off the existing row, so this cannot move a sign-in to a different shop.
    /// </remarks>
    private async Task ReinstateAsync(VanSalesCustomerAccountModel account)
    {
        if (isSaving)
        {
            return;
        }

        isSaving = true;

        try
        {
            var result = await Mediator.Send(
                new OnboardVanSalesCustomerAccountCommand(
                    account.RouteCustomerId,
                    account.PhoneE164,
                    account.DisplayName),
                disposeCts.Token);

            if (result.IsError)
            {
                Snackbar.Add(result.FirstError.Description, Severity.Error);
                return;
            }

            Snackbar.Add($"{account.RouteCustomerName} can sign in again.", Severity.Success);
            await LoadAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    private void BeginWithdraw(VanSalesCustomerAccountModel account)
    {
        accountPendingWithdrawal = account;
        withdrawErrorMessage = null;
    }

    private void CancelWithdraw()
    {
        accountPendingWithdrawal = null;
        withdrawErrorMessage = null;
    }

    private async Task ConfirmWithdrawAsync()
    {
        if (accountPendingWithdrawal is null || isWithdrawing)
        {
            return;
        }

        isWithdrawing = true;
        withdrawErrorMessage = null;

        try
        {
            var result = await Mediator.Send(
                new DeactivateVanSalesCustomerAccountCommand(accountPendingWithdrawal.Id),
                disposeCts.Token);

            if (result.IsError)
            {
                withdrawErrorMessage = result.FirstError.Description;
                return;
            }

            Snackbar.Add(
                $"{result.Value.RouteCustomerName} has been signed out and can no longer order.",
                Severity.Success);

            accountPendingWithdrawal = null;
            await LoadAsync();
        }
        finally
        {
            isWithdrawing = false;
        }
    }

    /// <summary>
    /// The badge's hue. Active takes the accent rather than the good family, which is the design's
    /// reading and the right one: on this list active is the ordinary state of nearly every row, and
    /// a column of green ticks would spend the alarm colours on saying "normal".
    /// </summary>
    private static string StatusFamily(VanSalesCustomerAccountModel account) => account switch
    {
        { IsActive: false } => "caa-fam-neutral",
        { IsLockedOut: true } => "caa-fam-bad",
        _ => "caa-fam-accent"
    };

    private static string StatusLabel(VanSalesCustomerAccountModel account) => account switch
    {
        { IsActive: false } => "Withdrawn",
        { IsLockedOut: true } => "Locked out",
        _ => "Active"
    };

    /// <summary>
    /// When the shop last signed in, or that it never has.
    /// </summary>
    /// <remarks>
    /// Relative for the first week and a date after that, which is the design's treatment and the
    /// one that reads: "4 days ago" answers the question the column is scanned for, while a date
    /// three weeks old has to be subtracted from today before it means anything.
    /// <para>
    /// "Not yet" is the useful answer for a sign-in nobody has used: it separates a shop that was
    /// set up and never got going from one that is ordering, which is the difference between a
    /// follow-up call and no action.
    /// </para>
    /// </remarks>
    private static string LastSeen(VanSalesCustomerAccountModel account)
    {
        if (account.LastLoginAt is not { } lastLogin)
        {
            return "Not yet";
        }

        var local = lastLogin.ToLocalTime();
        var days = (DateTime.Today - local.Date).Days;

        // A negative count means a clock ahead of this one; it falls to the date, which is the
        // answer that cannot be read as nonsense.
        return days switch
        {
            0 => $"Today, {local:HH:mm}",
            1 => $"Yesterday, {local:HH:mm}",
            > 1 and < 7 => $"{days} days ago",
            _ => local.ToString("d MMM yyyy")
        };
    }
}
