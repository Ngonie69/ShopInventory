using MediatR;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using ShopInventory.Web.Components;
using ShopInventory.Web.Features.SapUserAccounts.Commands.ChangeSapUserPassword;
using ShopInventory.Web.Features.SapUserAccounts.Commands.UnlockSapUserAccount;
using ShopInventory.Web.Features.SapUserAccounts.Queries.GetSapUserAccounts;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The SAP Business One user accounts, and the two things an administrator does with one when
/// somebody cannot get in: clear the lock, and set a password.
/// </summary>
/// <remarks>
/// Everything here is read live from SAP through the API — there is no local mirror of SAP's users,
/// and there deliberately isn't one. The question the screen answers is whether an account is locked
/// <em>now</em>, and a mirror answers that wrongly at exactly the moment somebody is standing at the
/// desk waiting. So both writes reload the whole list from SAP rather than patching the row on
/// screen.
/// </remarks>
public partial class SapUserAccounts : IDisposable
{
    /// <summary>How long the typing rests before the list is read again.</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(300);

    private static readonly NocturneSelectOption<bool>[] LockFilterOptions =
    [
        new(false, "All accounts", "neutral") { RuleAfter = true, IsUnset = true },
        new(true, "Locked only", "bad")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private ILogger<SapUserAccounts> Logger { get; set; } = default!;

    private SapUserAccountListModel accounts = new();

    private string searchTerm = string.Empty;
    private bool lockedOnly;

    private bool isLoading = true;
    private bool isSaving;

    /// <summary>Why the list is empty, when it is empty because something failed rather than because SAP has nobody.</summary>
    private string? loadError;

    /// <summary>The account both dialogs act on. One field because only one dialog is ever open.</summary>
    private SapUserAccountModel? actionTarget;

    private bool showUnlockModal;
    private bool showPasswordModal;

    /// <summary>
    /// What went wrong inside the open dialog. Kept on the dialog rather than raised as a toast: a
    /// SAP password-policy refusal is something to read and correct with the field still in front of
    /// you, and a toast takes it away after four seconds.
    /// </summary>
    private string? actionError;

    private ChangeSapUserPasswordModel passwordModel = new();

    /// <summary>Cancels the debounce when the next keystroke arrives, and on dispose.</summary>
    private CancellationTokenSource? searchDebounce;

    /// <summary>Cancels the in-flight read when a newer one starts, and on dispose.</summary>
    private CancellationTokenSource? loadCancellation;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task ReloadAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        // A read in flight is answering a filter the operator has already moved on from.
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        var cancellationToken = loadCancellation.Token;

        isLoading = true;
        loadError = null;
        StateHasChanged();

        try
        {
            var result = await Mediator.Send(
                new GetSapUserAccountsQuery(searchTerm, lockedOnly), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result.IsError)
            {
                loadError = result.FirstError.Description;
                accounts = new SapUserAccountListModel();
            }
            else
            {
                accounts = result.Value;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading SAP user accounts");
            loadError = "SAP user accounts could not be loaded.";
            accounts = new SapUserAccountListModel();
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                isLoading = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Re-reads after the typing rests. The search is pushed to SAP rather than applied to the rows
    /// in hand, because the rows in hand may be a truncated first page — filtering those would hide
    /// the account being searched for and say "no accounts found" about somebody who exists.
    /// </summary>
    private async Task OnSearchChangedAsync()
    {
        searchDebounce?.Cancel();
        searchDebounce?.Dispose();
        searchDebounce = new CancellationTokenSource();
        var token = searchDebounce.Token;

        try
        {
            await Task.Delay(SearchDebounce, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            await LoadAsync();
        }
    }

    private static string AvatarInitial(SapUserAccountModel account)
    {
        var source = string.IsNullOrWhiteSpace(account.DisplayName) ? "?" : account.DisplayName.Trim();
        return source[0].ToString().ToUpperInvariant();
    }

    private void ShowUnlockModal(SapUserAccountModel account)
    {
        actionTarget = account;
        actionError = null;
        showUnlockModal = true;
    }

    private void CloseUnlockModal()
    {
        showUnlockModal = false;
        actionTarget = null;
        actionError = null;
    }

    private void ShowPasswordModal(SapUserAccountModel account)
    {
        actionTarget = account;
        actionError = null;
        passwordModel = new ChangeSapUserPasswordModel();
        showPasswordModal = true;
    }

    private void ClosePasswordModal()
    {
        showPasswordModal = false;
        actionTarget = null;
        actionError = null;
        // The typed password does not outlive the dialog.
        passwordModel = new ChangeSapUserPasswordModel();
    }

    private async Task UnlockAsync()
    {
        if (actionTarget is null || isSaving)
        {
            return;
        }

        isSaving = true;
        actionError = null;

        try
        {
            var result = await Mediator.Send(
                new UnlockSapUserAccountCommand(actionTarget.InternalKey, actionTarget.UserCode));

            if (result.IsError)
            {
                actionError = result.FirstError.Description;
                return;
            }

            var userCode = result.Value.UserCode;
            CloseUnlockModal();
            Snackbar.Add($"SAP user {userCode} is unlocked.", Severity.Success);

            await LoadAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task ChangePasswordAsync()
    {
        if (actionTarget is null || isSaving)
        {
            return;
        }

        isSaving = true;
        actionError = null;

        try
        {
            var result = await Mediator.Send(
                new ChangeSapUserPasswordCommand(
                    actionTarget.InternalKey, actionTarget.UserCode, passwordModel.NewPassword));

            if (result.IsError)
            {
                actionError = result.FirstError.Description;
                return;
            }

            var userCode = result.Value.UserCode;
            ClosePasswordModal();
            Snackbar.Add($"The password for SAP user {userCode} has been changed.", Severity.Success);

            await LoadAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    public void Dispose()
    {
        searchDebounce?.Cancel();
        searchDebounce?.Dispose();
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
    }
}
