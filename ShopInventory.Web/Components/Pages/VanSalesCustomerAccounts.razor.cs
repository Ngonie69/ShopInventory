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

    private const string ShowWithdrawnFilter = "show-withdrawn";
    private const string ActiveOnlyFilter = "active-only";

    /// <summary>
    /// Withdrawn sign-ins are shown by default.
    /// </summary>
    /// <remarks>
    /// The question this screen is opened with is usually "why can this shop not order?", and a
    /// withdrawn account hidden from the list looks identical to one that was never created — which
    /// sends the operator to create a second one rather than reinstate the first.
    /// <para>
    /// Held as a string rather than the bool it means, because a <c>select</c> bound to a bool
    /// renders no selection at all: .NET formats <c>true</c> as "True" and no option value matches
    /// it, so the control comes up blank while the filter is in fact on. The other filter screens
    /// back their selects with strings for the same reason.
    /// </para>
    /// </remarks>
    private string withdrawnFilter = ShowWithdrawnFilter;

    private bool IncludeInactive => withdrawnFilter == ShowWithdrawnFilter;

    private bool isLoading = true;
    private bool isSaving;

    private VanSalesCustomerAccountModel? accountPendingWithdrawal;
    private bool isWithdrawing;
    private string? withdrawErrorMessage;

    private string? formErrorMessage;

    private IEnumerable<VanSalesCustomerAccountModel> FilteredAccounts =>
        view.Accounts.Where(account =>
            string.IsNullOrWhiteSpace(searchTerm) ||
            account.RouteCustomerName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            account.RouteCustomerCode.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            account.PhoneE164.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            (account.DisplayName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false));

    private int ActiveCount => view.Accounts.Count(a => a.IsActive);

    protected override Task OnInitializedAsync() => LoadAsync();

    public void Dispose()
    {
        disposeCts.Cancel();
        disposeCts.Dispose();
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

    private static string StatusFamily(VanSalesCustomerAccountModel account) => account switch
    {
        { IsActive: false } => "ops-fam-neutral",
        { IsLockedOut: true } => "ops-fam-bad",
        _ => "ops-fam-good"
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
    /// "Not yet" is the useful answer for a sign-in nobody has used: it separates a shop that was
    /// set up and never got going from one that is ordering, which is the difference between a
    /// follow-up call and no action.
    /// </remarks>
    private static string LastSeen(VanSalesCustomerAccountModel account) =>
        account.LastLoginAt is { } lastLogin
            ? lastLogin.ToLocalTime().ToString("d MMM yyyy")
            : "Not yet";
}
