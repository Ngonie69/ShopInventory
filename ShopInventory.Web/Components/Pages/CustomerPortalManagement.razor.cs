using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class CustomerPortalManagement : ComponentBase
{
    private const string StatusActive = "active";
    private const string StatusInactive = "inactive";
    private const string StatusLocked = "locked";
    private const string FilterAll = "all";

    private const string SortName = "name";
    private const string SortCode = "code";
    private const string SortStatus = "status";
    private const string SortLastLogin = "lastLogin";

    private static readonly (string Value, string Label)[] StatusFilters =
    [
        (FilterAll, "All"),
        (StatusActive, "Active"),
        (StatusInactive, "Inactive"),
        (StatusLocked, "Locked")
    ];

    private static readonly StringComparer TextComparer = StringComparer.OrdinalIgnoreCase;

    [Inject] private IDbContextFactory<WebAppDbContext> DbContextFactory { get; set; } = null!;
    [Inject] private ICustomerLinkedAccountService LinkedAccountService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private ILogger<CustomerPortalManagement> Logger { get; set; } = null!;

    // The whole account list is held in memory and filtered here. Portal
    // accounts are admin-configured records numbering in the dozens, and the
    // design asks for live search, per-status counts and sortable columns —
    // all three of which would otherwise be a round trip each.
    private List<CustomerPortalUser> accounts = [];
    private bool isLoading = true;
    private bool isSaving;
    private bool isExporting;
    private DateTime? lastSyncedAt;
    private string? successMessage;
    private string? errorMessage;

    private string searchTerm = string.Empty;
    private string statusFilter = FilterAll;
    private string sortKey = SortName;
    private bool sortDescending;

    private int? selectedId;
    private CachedBusinessPartner? selectedPartner;

    // Create / edit dialog
    private bool showFormDialog;
    private bool isEditing;
    private bool showPassword;
    private AccountForm form = new();

    // The business-partner picker inside the create dialog
    private string pickerTerm = string.Empty;
    private List<CachedBusinessPartner> pickerResults = [];
    private bool isPickerSearching;
    private CachedBusinessPartner? pickedPartner;
    private int pickerVersion;

    // Reset password dialog
    private bool showResetDialog;
    private string newPassword = string.Empty;
    private bool showNewPassword;

    // Delete dialog
    private bool showDeleteDialog;

    // Activity dialog
    private bool showActivityDialog;
    private bool isLoadingActivity;
    private List<CustomerSecurityLog> activity = [];

    // Linked accounts dialog
    private bool showLinkedDialog;
    private List<LinkedAccountInfo>? linkedAccounts;
    private string? linkedMessage;
    private bool linkedMessageIsError;
    private bool showBulkAdd;

    private string linkedPickerTerm = string.Empty;
    private List<CachedBusinessPartner> linkedPickerResults = [];
    private CachedBusinessPartner? linkedPickedPartner;
    private int linkedPickerVersion;
    private string newLinkedType = "Main";
    private string newLinkedCurrency = string.Empty;
    private string newLinkedParent = string.Empty;
    private string newLinkedDescription = string.Empty;

    private string bulkTerm = string.Empty;
    private List<CachedBusinessPartner>? bulkResults;
    private readonly HashSet<string> bulkSelected = new(StringComparer.OrdinalIgnoreCase);
    private string bulkCurrency = string.Empty;
    private string bulkParent = string.Empty;
    private string bulkDescription = string.Empty;
    private bool isBulkSearching;
    private bool isBulkAdding;

    private CustomerPortalUser? Selected =>
        selectedId is null ? null : accounts.FirstOrDefault(a => a.Id == selectedId);

    private List<CustomerPortalUser> Filtered
    {
        get
        {
            var rows = accounts
                .Where(a => statusFilter == FilterAll || StatusOf(a) == statusFilter)
                .Where(MatchesSearch);

            var ordered = sortKey switch
            {
                SortCode => rows.OrderBy(a => a.CardCode, TextComparer),
                SortStatus => rows.OrderBy(a => StatusRank(a)).ThenBy(a => a.CardName, TextComparer),
                // Never-signed-in sorts as the oldest, so ascending puts it first
                // and descending puts the most recent sign-in at the top.
                SortLastLogin => rows.OrderBy(a => a.LastLoginAt ?? DateTime.MinValue),
                _ => rows.OrderBy(a => a.CardName, TextComparer)
            };

            var list = ordered.ToList();
            if (sortDescending)
                list.Reverse();

            return list;
        }
    }

    private int TotalCount => accounts.Count;
    private int ActiveCount => CountOf(StatusActive);
    private int InactiveCount => CountOf(StatusInactive);
    private int LockedCount => CountOf(StatusLocked);

    private int ActivePercent =>
        TotalCount == 0 ? 0 : (int)Math.Round(ActiveCount * 100.0 / TotalCount);

    private int CountOf(string status) => accounts.Count(a => StatusOf(a) == status);

    // The three statuses are exclusive and a locked account is locked whatever
    // its IsActive flag says, so the three figures always sum to the total.
    private static string StatusOf(CustomerPortalUser account) =>
        account.LockedUntil.HasValue && account.LockedUntil > DateTime.UtcNow ? StatusLocked
        : account.IsActive ? StatusActive
        : StatusInactive;

    private static int StatusRank(CustomerPortalUser account) => StatusOf(account) switch
    {
        StatusActive => 0,
        StatusInactive => 1,
        _ => 2
    };

    private static string StatusLabel(string status) => status switch
    {
        StatusActive => "Active",
        StatusInactive => "Inactive",
        _ => "Locked"
    };

    private bool MatchesSearch(CustomerPortalUser account)
    {
        var term = searchTerm.Trim();
        if (term.Length == 0)
            return true;

        return Contains(account.CardCode, term)
            || Contains(account.CardName, term)
            || Contains(account.Email, term)
            || account.LinkedAccounts.Any(l => Contains(l.CardCode, term) || Contains(l.CardName, term));
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        await LoadAccounts();
    }

    private async Task LoadAccounts()
    {
        isLoading = true;
        errorMessage = null;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();

            accounts = await db.Set<CustomerPortalUser>()
                .AsNoTracking()
                .Include(a => a.LinkedAccounts)
                .OrderBy(a => a.CardCode)
                .ToListAsync();

            lastSyncedAt = DateTime.Now;

            // A row that has gone away should not leave a stale detail panel.
            if (selectedId is not null && accounts.All(a => a.Id != selectedId))
                selectedId = null;

            await LoadSelectedPartner();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading customer portal accounts");
            errorMessage = "We couldn't load the portal accounts. Please try again.";
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task Refresh()
    {
        searchTerm = string.Empty;
        statusFilter = FilterAll;
        await LoadAccounts();
    }

    private async Task SelectAccount(CustomerPortalUser account)
    {
        selectedId = account.Id;
        await LoadSelectedPartner();
    }

    // The facts panel shows what SAP knows about the card code — phone,
    // currency and balance — which lives on the cached partner rather than on
    // the portal account. The cache holds active partners only, so a missing
    // row is expected and reads as an em dash.
    private async Task LoadSelectedPartner()
    {
        selectedPartner = null;

        var account = Selected;
        if (account is null)
            return;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            selectedPartner = await db.CachedBusinessPartners
                .AsNoTracking()
                .FirstOrDefaultAsync(bp => bp.CardCode == account.CardCode);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load the cached partner for {CardCode}", account.CardCode);
        }
    }

    private void SetStatusFilter(string value)
    {
        statusFilter = value;
    }

    private void SortBy(string key)
    {
        if (sortKey == key)
        {
            sortDescending = !sortDescending;
            return;
        }

        sortKey = key;
        sortDescending = false;
    }

    private string SortArrow(string key) => sortKey != key ? string.Empty : sortDescending ? "↓" : "↑";

    private static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";

        var words = new string(name.Select(c => char.IsLetter(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
            return "?";

        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string When(DateTime? value) =>
        value is null ? "Never" : value.Value.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    private static string Day(DateTime? value) =>
        value is null ? "—" : value.Value.ToLocalTime().ToString("d MMM yyyy");

    private static string StructureLabel(CustomerPortalUser account) =>
        account.AccountStructure == "Multi"
            ? $"Multi-account · {account.LinkedAccounts.Count} linked"
            : "Single account";

    private string Money(decimal? amount) =>
        amount is null ? "—" : amount.Value.ToString("N2", CultureInfo.CurrentCulture);

    /* ── Statement and two-factor toggles ─────────────────────────────── */

    private Task ToggleStatements(CustomerPortalUser account) =>
        UpdateAccount(account, existing =>
        {
            existing.ReceiveStatements = !existing.ReceiveStatements;
            return existing.ReceiveStatements
                ? $"Statement emails enabled for {existing.CardCode}."
                : $"Statement emails disabled for {existing.CardCode}.";
        });

    private Task ToggleTwoFactor(CustomerPortalUser account) =>
        UpdateAccount(account, existing =>
        {
            existing.TwoFactorEnabled = !existing.TwoFactorEnabled;
            return existing.TwoFactorEnabled
                ? $"Two-factor sign-in required for {existing.CardCode}."
                : $"Two-factor sign-in switched off for {existing.CardCode}.";
        });

    private Task ToggleSuspended(CustomerPortalUser account) =>
        UpdateAccount(account, existing =>
        {
            existing.IsActive = !existing.IsActive;
            existing.Status = existing.IsActive ? "Active" : "Suspended";
            return existing.IsActive
                ? $"Access restored for {existing.CardCode}."
                : $"Access suspended for {existing.CardCode}.";
        });

    private Task UnlockAccount(CustomerPortalUser account) =>
        UpdateAccount(account, existing =>
        {
            existing.LockedUntil = null;
            existing.FailedLoginAttempts = 0;
            existing.Status = existing.IsActive ? "Active" : "Suspended";
            return $"Account {existing.CardCode} unlocked.";
        });

    private async Task UpdateAccount(CustomerPortalUser account, Func<CustomerPortalUser, string> mutate)
    {
        isSaving = true;
        errorMessage = null;
        successMessage = null;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();

            var existing = await db.Set<CustomerPortalUser>().FirstOrDefaultAsync(a => a.Id == account.Id);
            if (existing is null)
            {
                errorMessage = "That account no longer exists. Refreshing the list.";
                await LoadAccounts();
                return;
            }

            var message = mutate(existing);
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            successMessage = message;
            await LoadAccounts();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error updating portal account {CardCode}", account.CardCode);
            errorMessage = "We couldn't save that change. Please try again.";
        }
        finally
        {
            isSaving = false;
        }
    }

    /* ── Create and edit ──────────────────────────────────────────────── */

    private void OpenCreateDialog()
    {
        form = new AccountForm { IsActive = true, ReceiveStatements = true };
        pickedPartner = null;
        pickerTerm = string.Empty;
        pickerResults = [];
        isEditing = false;
        showPassword = false;
        showFormDialog = true;
    }

    private void OpenEditDialog(CustomerPortalUser account)
    {
        form = new AccountForm
        {
            Id = account.Id,
            CardCode = account.CardCode,
            CardName = account.CardName,
            Email = account.Email ?? string.Empty,
            IsActive = account.IsActive,
            TwoFactorEnabled = account.TwoFactorEnabled,
            ReceiveStatements = account.ReceiveStatements
        };
        isEditing = true;
        showPassword = false;
        showFormDialog = true;
    }

    private void CloseFormDialog()
    {
        showFormDialog = false;
        form = new AccountForm();
        pickedPartner = null;
        pickerResults = [];
    }

    private async Task OnPickerInput(ChangeEventArgs e)
    {
        pickerTerm = e.Value?.ToString() ?? string.Empty;
        pickerResults = await SearchPartners(pickerTerm, ++pickerVersion, () => pickerVersion, excludeExisting: true,
            searching => isPickerSearching = searching);
    }

    private void PickPartner(CachedBusinessPartner partner)
    {
        pickedPartner = partner;
        form.CardCode = partner.CardCode;
        form.CardName = partner.CardName ?? string.Empty;
        form.Email = partner.Email ?? string.Empty;
        pickerResults = [];
        pickerTerm = string.Empty;
    }

    private void ClearPickedPartner()
    {
        pickedPartner = null;
        form.CardCode = string.Empty;
        form.CardName = string.Empty;
        form.Email = string.Empty;
    }

    // One debounced search behind both pickers. The version counter is the
    // debounce: a keystroke that arrives while this one is waiting bumps it,
    // and the older call drops its result rather than overwriting the newer.
    private async Task<List<CachedBusinessPartner>> SearchPartners(
        string term,
        int version,
        Func<int> currentVersion,
        bool excludeExisting,
        Action<bool> setSearching)
    {
        term = term.Trim();
        if (term.Length < 2)
        {
            setSearching(false);
            return [];
        }

        setSearching(true);
        StateHasChanged();

        try
        {
            await Task.Delay(250);
            if (currentVersion() != version)
                return [];

            await using var db = await DbContextFactory.CreateDbContextAsync();

            var query = db.CachedBusinessPartners
                .AsNoTracking()
                .Where(bp => bp.CardCode.ToLower().Contains(term.ToLower())
                    || (bp.CardName != null && bp.CardName.ToLower().Contains(term.ToLower())));

            if (excludeExisting)
            {
                query = query.Where(bp => !db.Set<CustomerPortalUser>().Any(a => a.CardCode == bp.CardCode));
            }

            var results = await query.OrderBy(bp => bp.CardCode).Take(15).ToListAsync();
            return currentVersion() == version ? results : [];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error searching business partners for '{Term}'", term);
            return [];
        }
        finally
        {
            if (currentVersion() == version)
                setSearching(false);
        }
    }

    private async Task SaveAccount()
    {
        if (string.IsNullOrWhiteSpace(form.CardCode) ||
            string.IsNullOrWhiteSpace(form.CardName) ||
            string.IsNullOrWhiteSpace(form.Email))
        {
            errorMessage = "Card code, customer name and email are all required.";
            return;
        }

        if (!isEditing)
        {
            if (form.Password != form.ConfirmPassword)
            {
                errorMessage = "The two passwords don't match.";
                return;
            }

            if (!IsPasswordStrong(form.Password))
            {
                errorMessage = "The password needs at least 8 characters with an uppercase letter, "
                    + "a lowercase letter, a number and a symbol.";
                return;
            }
        }

        isSaving = true;
        errorMessage = null;
        successMessage = null;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();

            if (isEditing)
            {
                var existing = await db.Set<CustomerPortalUser>().FirstOrDefaultAsync(a => a.Id == form.Id);
                if (existing is null)
                {
                    errorMessage = "That account no longer exists.";
                    return;
                }

                existing.CardName = form.CardName.Trim();
                existing.Email = form.Email.Trim();
                existing.IsActive = form.IsActive;
                existing.TwoFactorEnabled = form.TwoFactorEnabled;
                existing.ReceiveStatements = form.ReceiveStatements;
                existing.Status = form.IsActive ? "Active" : "Suspended";
                existing.UpdatedAt = DateTime.UtcNow;

                await db.SaveChangesAsync();
                successMessage = $"Portal account {existing.CardCode} updated.";
            }
            else
            {
                var cardCode = form.CardCode.Trim();

                if (await db.Set<CustomerPortalUser>().AnyAsync(a => a.CardCode == cardCode))
                {
                    errorMessage = $"A portal account for {cardCode} already exists.";
                    return;
                }

                var created = new CustomerPortalUser
                {
                    CardCode = cardCode,
                    CardName = form.CardName.Trim(),
                    Email = form.Email.Trim(),
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(form.Password, 12),
                    IsActive = form.IsActive,
                    TwoFactorEnabled = form.TwoFactorEnabled,
                    ReceiveStatements = form.ReceiveStatements,
                    Status = form.IsActive ? "Active" : "Suspended",
                    EmailVerified = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                db.Set<CustomerPortalUser>().Add(created);
                await db.SaveChangesAsync();

                selectedId = created.Id;
                successMessage = $"Portal account {cardCode} created.";
            }

            CloseFormDialog();
            await LoadAccounts();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving portal account {CardCode}", form.CardCode);
            errorMessage = "We couldn't save the account. Please try again.";
        }
        finally
        {
            isSaving = false;
        }
    }

    /* ── Password reset ───────────────────────────────────────────────── */

    private void OpenResetDialog()
    {
        newPassword = string.Empty;
        showNewPassword = false;
        showResetDialog = true;
    }

    private async Task ResetPassword()
    {
        var account = Selected;
        if (account is null)
            return;

        if (!IsPasswordStrong(newPassword))
        {
            errorMessage = "The password needs at least 8 characters with an uppercase letter, "
                + "a lowercase letter, a number and a symbol.";
            return;
        }

        isSaving = true;
        errorMessage = null;
        successMessage = null;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();

            var existing = await db.Set<CustomerPortalUser>().FirstOrDefaultAsync(a => a.Id == account.Id);
            if (existing is not null)
            {
                existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, 12);
                existing.LastPasswordChangeAt = DateTime.UtcNow;
                existing.PasswordExpiresAt = DateTime.UtcNow.AddDays(90);
                existing.UpdatedAt = DateTime.UtcNow;
                existing.FailedLoginAttempts = 0;
                existing.LockedUntil = null;
                existing.MustChangePassword = false;
                existing.PasswordResetToken = null;
                existing.PasswordResetTokenExpiry = null;
                existing.Status = existing.IsActive ? "Active" : "Suspended";

                await db.SaveChangesAsync();
                successMessage = $"Password reset for {existing.CardCode}.";
            }

            showResetDialog = false;
            newPassword = string.Empty;
            await LoadAccounts();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error resetting the password for {CardCode}", account.CardCode);
            errorMessage = "We couldn't reset the password. Please try again.";
        }
        finally
        {
            isSaving = false;
        }
    }

    private void GenerateFormPassword()
    {
        form.Password = GenerateStrongPassword();
        form.ConfirmPassword = form.Password;
        showPassword = true;
    }

    private void GenerateResetPassword()
    {
        newPassword = GenerateStrongPassword();
        showNewPassword = true;
    }

    // What this returns becomes a customer's actual portal credential, so every
    // draw is cryptographic — including the shuffle. `Random`/`Random.Shared` is
    // seeded predictably enough that two passwords issued close together are
    // related, which is what CodeQL's insecure-randomness rule is about; the
    // page this replaced used `new Random()` throughout and had the same flaw.
    // RandomNumberGenerator.GetInt32 is also rejection-sampled, so it does not
    // carry the modulo bias a `% length` would.
    //
    // Ambiguous glyphs are left out on purpose: these are read off a screen and
    // typed into a phone by whoever the admin passes them to.
    private static string GenerateStrongPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghjkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*";

        var chars = new char[12];

        // One of each class up front, so the result always clears
        // IsPasswordStrong; the shuffle below is what stops those four sitting
        // in a known order.
        chars[0] = Pick(upper);
        chars[1] = Pick(lower);
        chars[2] = Pick(digits);
        chars[3] = Pick(symbols);

        var all = upper + lower + digits + symbols;
        for (var i = 4; i < chars.Length; i++)
            chars[i] = Pick(all);

        // Fisher-Yates, drawing from the same source: an OrderBy on a random key
        // would put the choice of permutation back on a weaker generator.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

    private static bool IsPasswordStrong(string? password) =>
        !string.IsNullOrEmpty(password)
        && password.Length >= 8
        && password.Any(char.IsUpper)
        && password.Any(char.IsLower)
        && password.Any(char.IsDigit)
        && password.Any(c => !char.IsLetterOrDigit(c));

    /* ── Delete ───────────────────────────────────────────────────────── */

    private void OpenDeleteDialog() => showDeleteDialog = true;

    private async Task DeleteAccount()
    {
        var account = Selected;
        if (account is null)
            return;

        isSaving = true;
        errorMessage = null;
        successMessage = null;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();

            var existing = await db.Set<CustomerPortalUser>().FirstOrDefaultAsync(a => a.Id == account.Id);
            if (existing is not null)
            {
                db.Set<CustomerPortalUser>().Remove(existing);
                await db.SaveChangesAsync();
                successMessage = $"Portal account {existing.CardCode} deleted.";
            }

            showDeleteDialog = false;
            selectedId = null;
            await LoadAccounts();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error deleting portal account {CardCode}", account.CardCode);
            errorMessage = "We couldn't delete the account. Please try again.";
        }
        finally
        {
            isSaving = false;
        }
    }

    /* ── Activity ─────────────────────────────────────────────────────── */

    private async Task OpenActivityDialog()
    {
        var account = Selected;
        if (account is null)
            return;

        showActivityDialog = true;
        isLoadingActivity = true;
        activity = [];

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();

            activity = await db.CustomerSecurityLogs
                .AsNoTracking()
                .Where(log => log.CardCode == account.CardCode)
                .OrderByDescending(log => log.Timestamp)
                .Take(50)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading the activity log for {CardCode}", account.CardCode);
            errorMessage = "We couldn't load the activity for that account.";
            showActivityDialog = false;
        }
        finally
        {
            isLoadingActivity = false;
        }
    }

    /* ── Linked accounts ──────────────────────────────────────────────── */

    private async Task OpenLinkedDialog()
    {
        if (Selected is null)
            return;

        linkedMessage = null;
        linkedPickedPartner = null;
        linkedPickerTerm = string.Empty;
        linkedPickerResults = [];
        newLinkedType = "Main";
        newLinkedCurrency = string.Empty;
        newLinkedParent = string.Empty;
        newLinkedDescription = string.Empty;
        showBulkAdd = false;
        bulkResults = null;
        bulkSelected.Clear();
        bulkTerm = string.Empty;
        bulkCurrency = string.Empty;
        bulkParent = string.Empty;
        bulkDescription = string.Empty;
        showLinkedDialog = true;

        await LoadLinkedAccounts();
    }

    private async Task CloseLinkedDialog()
    {
        showLinkedDialog = false;
        linkedAccounts = null;

        // The structure and the linked codes are shown on the row and in the
        // detail panel, so the list has to catch up with whatever changed here.
        await LoadAccounts();
    }

    private async Task LoadLinkedAccounts()
    {
        var account = Selected;
        if (account is null)
            return;

        try
        {
            linkedAccounts = await LinkedAccountService.GetLinkedAccountsAsync(account.CardCode);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error loading linked accounts for {CardCode}", account.CardCode);
            linkedMessage = "We couldn't load the linked accounts.";
            linkedMessageIsError = true;
        }
    }

    private async Task OnLinkedPickerInput(ChangeEventArgs e)
    {
        linkedPickerTerm = e.Value?.ToString() ?? string.Empty;
        linkedPickerResults = await SearchPartners(linkedPickerTerm, ++linkedPickerVersion,
            () => linkedPickerVersion, excludeExisting: false, _ => { });
    }

    private void PickLinkedPartner(CachedBusinessPartner partner)
    {
        linkedPickedPartner = partner;
        linkedPickerResults = [];
        linkedPickerTerm = string.Empty;
    }

    private async Task AddLinkedAccount()
    {
        var account = Selected;
        if (account is null)
            return;

        if (linkedPickedPartner is null)
        {
            linkedMessage = "Pick a business partner first.";
            linkedMessageIsError = true;
            return;
        }

        if (newLinkedType == "Sub" && string.IsNullOrEmpty(newLinkedParent))
        {
            linkedMessage = "A sub account needs a parent main account.";
            linkedMessageIsError = true;
            return;
        }

        isSaving = true;

        try
        {
            var result = await LinkedAccountService.AddLinkedAccountAsync(account.CardCode, new LinkedAccountRequest
            {
                CardCode = linkedPickedPartner.CardCode,
                CardName = linkedPickedPartner.CardName ?? string.Empty,
                AccountType = newLinkedType,
                Currency = string.IsNullOrEmpty(newLinkedCurrency) ? null : newLinkedCurrency,
                ParentCardCode = newLinkedType == "Sub" ? newLinkedParent : null,
                Description = string.IsNullOrWhiteSpace(newLinkedDescription) ? null : newLinkedDescription
            });

            linkedMessage = result.Message;
            linkedMessageIsError = !result.Success;

            if (result.Success)
            {
                linkedAccounts = result.LinkedAccounts;
                linkedPickedPartner = null;
                newLinkedType = "Main";
                newLinkedCurrency = string.Empty;
                newLinkedParent = string.Empty;
                newLinkedDescription = string.Empty;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding a linked account to {CardCode}", account.CardCode);
            linkedMessage = "We couldn't add that linked account.";
            linkedMessageIsError = true;
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task RemoveLinkedAccount(string linkedCardCode)
    {
        var account = Selected;
        if (account is null)
            return;

        isSaving = true;

        try
        {
            var result = await LinkedAccountService.RemoveLinkedAccountAsync(account.CardCode, linkedCardCode);
            linkedMessage = result.Message;
            linkedMessageIsError = !result.Success;

            if (result.Success)
                linkedAccounts = result.LinkedAccounts;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error removing linked account {Linked} from {CardCode}",
                linkedCardCode, account.CardCode);
            linkedMessage = "We couldn't remove that linked account.";
            linkedMessageIsError = true;
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task ConvertToSingleAccount()
    {
        var account = Selected;
        if (account is null)
            return;

        isSaving = true;

        try
        {
            var result = await LinkedAccountService.ConvertToSingleAccountAsync(account.CardCode);
            linkedMessage = result.Message;
            linkedMessageIsError = !result.Success;

            if (result.Success)
                linkedAccounts = result.LinkedAccounts;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error converting {CardCode} back to a single account", account.CardCode);
            linkedMessage = "We couldn't convert that account.";
            linkedMessageIsError = true;
        }
        finally
        {
            isSaving = false;
        }
    }

    private List<LinkedAccountInfo> MainAccounts =>
        linkedAccounts?.Where(a => a.AccountType == "Main").ToList() ?? [];

    private async Task BulkSearch()
    {
        if (string.IsNullOrWhiteSpace(bulkTerm))
            return;

        isBulkSearching = true;

        try
        {
            await using var db = await DbContextFactory.CreateDbContextAsync();
            var term = bulkTerm.Trim().ToLower();

            bulkResults = await db.CachedBusinessPartners
                .AsNoTracking()
                .Where(bp => bp.CardCode.ToLower().Contains(term)
                    || (bp.CardName != null && bp.CardName.ToLower().Contains(term)))
                .OrderBy(bp => bp.CardCode)
                .Take(50)
                .ToListAsync();

            var codes = bulkResults.Select(bp => bp.CardCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bulkSelected.RemoveWhere(code => !codes.Contains(code));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error searching business partners for the bulk add");
            linkedMessage = "We couldn't search the business partners.";
            linkedMessageIsError = true;
        }
        finally
        {
            isBulkSearching = false;
        }
    }

    private bool IsAlreadyLinked(string cardCode) =>
        linkedAccounts?.Any(a => string.Equals(a.CardCode, cardCode, StringComparison.OrdinalIgnoreCase)) ?? false;

    private void ToggleBulkSelect(string cardCode)
    {
        if (!bulkSelected.Remove(cardCode))
            bulkSelected.Add(cardCode);
    }

    private bool AllBulkSelected =>
        bulkResults is { Count: > 0 }
        && bulkResults.Where(bp => !IsAlreadyLinked(bp.CardCode))
            .Select(bp => bp.CardCode)
            .All(bulkSelected.Contains);

    private void ToggleSelectAllBulk()
    {
        if (bulkResults is null)
            return;

        var selectable = bulkResults
            .Where(bp => !IsAlreadyLinked(bp.CardCode))
            .Select(bp => bp.CardCode)
            .ToList();

        if (selectable.All(bulkSelected.Contains))
            bulkSelected.ExceptWith(selectable);
        else
            bulkSelected.UnionWith(selectable);
    }

    private async Task BulkAddLinkedAccounts()
    {
        var account = Selected;
        if (account is null || bulkSelected.Count == 0)
            return;

        if (string.IsNullOrEmpty(bulkParent))
        {
            linkedMessage = "Pick the parent main account these sub accounts belong to.";
            linkedMessageIsError = true;
            return;
        }

        isBulkAdding = true;
        var added = 0;
        var failed = 0;
        string? lastError = null;

        try
        {
            var partners = bulkResults?
                .Where(bp => bulkSelected.Contains(bp.CardCode))
                .ToList() ?? [];

            foreach (var partner in partners)
            {
                try
                {
                    var result = await LinkedAccountService.AddLinkedAccountAsync(account.CardCode,
                        new LinkedAccountRequest
                        {
                            CardCode = partner.CardCode,
                            CardName = partner.CardName ?? string.Empty,
                            AccountType = "Sub",
                            Currency = string.IsNullOrEmpty(bulkCurrency) ? null : bulkCurrency,
                            ParentCardCode = bulkParent,
                            Description = string.IsNullOrWhiteSpace(bulkDescription) ? null : bulkDescription
                        });

                    if (result.Success)
                    {
                        added++;
                        linkedAccounts = result.LinkedAccounts;
                    }
                    else
                    {
                        failed++;
                        lastError = result.Message;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    lastError = ex.Message;
                    Logger.LogError(ex, "Error bulk-adding linked account {CardCode}", partner.CardCode);
                }
            }

            linkedMessage = failed == 0
                ? $"Added {added} sub account{(added == 1 ? "" : "s")}."
                : $"Added {added}, {failed} failed. Last error: {lastError}";
            linkedMessageIsError = failed > 0;

            bulkSelected.Clear();
            bulkResults = null;
            bulkTerm = string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in the bulk linked-account add for {CardCode}", account.CardCode);
            linkedMessage = "The bulk add failed.";
            linkedMessageIsError = true;
        }
        finally
        {
            isBulkAdding = false;
        }
    }

    /* ── Export ───────────────────────────────────────────────────────── */

    private async Task ExportCsv(List<CustomerPortalUser> rows)
    {
        isExporting = true;

        try
        {
            var csv = new StringBuilder();
            csv.AppendLine("Card code,Customer,Email,Structure,Linked accounts,Status,Statements,Two-factor,Last login,Created");

            foreach (var account in rows)
            {
                csv.Append(Csv(account.CardCode)).Append(',')
                   .Append(Csv(account.CardName)).Append(',')
                   .Append(Csv(account.Email)).Append(',')
                   .Append(Csv(account.AccountStructure)).Append(',')
                   .Append(account.LinkedAccounts.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(Csv(StatusLabel(StatusOf(account)))).Append(',')
                   .Append(account.ReceiveStatements ? "On" : "Off").Append(',')
                   .Append(account.TwoFactorEnabled ? "On" : "Off").Append(',')
                   .Append(Csv(account.LastLoginAt?.ToLocalTime().ToString("s", CultureInfo.InvariantCulture))).Append(',')
                   .Append(Csv(account.CreatedAt.ToLocalTime().ToString("s", CultureInfo.InvariantCulture)))
                   .Append('\n');
            }

            // The BOM is what makes Excel read the file as UTF-8; without it,
            // customer names with accents arrive mangled.
            var bytes = new UTF8Encoding(true).GetBytes(csv.ToString());
            var fileName = $"PortalAccounts_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            await JS.InvokeVoidAsync("downloadFile", fileName, Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error exporting the portal account list");
            errorMessage = "We couldn't export the account list. Please try again.";
        }
        finally
        {
            isExporting = false;
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }

    private sealed class AccountForm
    {
        public int Id { get; set; }
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public bool TwoFactorEnabled { get; set; }
        public bool ReceiveStatements { get; set; } = true;
    }
}
