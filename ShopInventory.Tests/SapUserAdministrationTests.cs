using System.Net;
using System.Security.Claims;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.Features.SapUsers.Commands.ChangeSapUserPassword;
using ShopInventory.Features.SapUsers.Commands.UnlockSapUserAccount;
using ShopInventory.Features.SapUsers.Queries.GetSapUserAccounts;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP user administration: reading the company's accounts, clearing a lock, and setting a password.
/// </summary>
/// <remarks>
/// The two behaviours worth protecting here are the ones that look like polish and are not.
/// <para>
/// <b>The read-back.</b> SAP accepts a PATCH that changes nothing, so a 204 is not evidence an
/// account can sign in. Both writes read the account again and answer with what SAP then holds — and
/// an unlock is exactly the operation somebody walks away from believing worked.
/// </para>
/// <para>
/// <b>The password never leaves the request.</b> It is not in a log, not in the audit row, not in
/// the answer. The test that asserts it reads what the audit service was actually handed.
/// </para>
/// </remarks>
public sealed class SapUserAdministrationTests
{
    private static readonly Guid AdminUserId = Guid.Parse("7c1e0000-0000-0000-0000-0000000000a1");

    #region The list

    [Fact]
    public async Task List_maps_what_sap_holds_and_counts_the_locked()
    {
        var sap = new RecordingSapClient(
        [
            Account(4, "kmoyo", "Kudzai Moyo", locked: true),
            Account(9, "manager", "Site Manager", locked: false, superuser: true)
        ]);

        var result = await ListHandler(sap).Handle(new GetSapUserAccountsQuery(null, false), CancellationToken.None);

        Assert.False(result.IsError, Describe(result.Errors));
        Assert.Equal(2, result.Value.TotalCount);
        Assert.Equal(1, result.Value.LockedCount);
        Assert.False(result.Value.Truncated);

        var locked = result.Value.Items.Single(item => item.IsLocked);
        Assert.Equal(4, locked.InternalKey);
        Assert.Equal("kmoyo", locked.UserCode);
        Assert.Equal("Kudzai Moyo", locked.UserName);
        Assert.False(locked.IsSuperuser);

        Assert.True(result.Value.Items.Single(item => item.UserCode == "manager").IsSuperuser);
    }

    [Fact]
    public async Task List_passes_the_search_and_the_locked_filter_to_sap()
    {
        var sap = new RecordingSapClient([]);

        await ListHandler(sap).Handle(new GetSapUserAccountsQuery("moyo", true), CancellationToken.None);

        Assert.Equal("moyo", sap.RequestedSearch);
        Assert.True(sap.RequestedLockedOnly);
    }

    /// <summary>
    /// A full page means SAP had at least as many accounts as one read returns, so the counts
    /// describe the page rather than the company and the screen has to say so.
    /// </summary>
    [Fact]
    public async Task List_reports_a_full_page_as_truncated()
    {
        var full = Enumerable.Range(1, SapUserAccountLimits.PageSize)
            .Select(key => Account(key, $"user{key}", $"User {key}", locked: false))
            .ToList();

        var result = await ListHandler(new RecordingSapClient(full))
            .Handle(new GetSapUserAccountsQuery(null, false), CancellationToken.None);

        Assert.True(result.Value.Truncated);
    }

    [Fact]
    public async Task List_is_refused_when_sap_integration_is_off()
    {
        var result = await ListHandler(new RecordingSapClient([]), sapEnabled: false)
            .Handle(new GetSapUserAccountsQuery(null, false), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapUser.SapDisabled", result.FirstError.Code);
    }

    #endregion

    #region Unlock

    [Fact]
    public async Task Unlock_clears_the_lock_and_answers_with_what_sap_then_holds()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: true)]);
        var audit = new RecordingAuditService();

        var result = await UnlockHandler(sap, audit).Handle(new UnlockSapUserAccountCommand(4), CancellationToken.None);

        Assert.False(result.IsError, Describe(result.Errors));
        Assert.False(result.Value.IsLocked);
        Assert.Equal((4, false), sap.LockWrite);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.UnlockSapUser, entry.Action);
        Assert.Equal("SapUser", entry.EntityType);
        Assert.Equal("4", entry.EntityId);
        Assert.True(entry.Succeeded);
    }

    /// <summary>
    /// The account is read again after the write rather than assumed. A SAP that accepts the PATCH
    /// and leaves the account locked — which is what a non-superuser session silently did on one
    /// landscape — must not be reported as an unlock.
    /// </summary>
    [Fact]
    public async Task Unlock_answers_still_locked_when_sap_did_not_clear_it()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: true)])
        {
            IgnoreLockWrites = true
        };

        var result = await UnlockHandler(sap).Handle(new UnlockSapUserAccountCommand(4), CancellationToken.None);

        Assert.False(result.IsError, Describe(result.Errors));
        Assert.True(result.Value.IsLocked);
    }

    [Fact]
    public async Task Unlock_refuses_an_account_that_is_not_locked()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: false)]);

        var result = await UnlockHandler(sap).Handle(new UnlockSapUserAccountCommand(4), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapUser.NotLocked", result.FirstError.Code);
        Assert.Contains("kmoyo", result.FirstError.Description);
        Assert.Null(sap.LockWrite);
    }

    /// <summary>
    /// Conflict rather than Validation, so the sentence lands in the problem details' <c>detail</c>
    /// where every client already reads it, instead of in the validation dictionary.
    /// </summary>
    [Fact]
    public async Task Unlock_refusal_of_an_unlocked_account_is_a_conflict()
    {
        var result = await UnlockHandler(new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: false)]))
            .Handle(new UnlockSapUserAccountCommand(4), CancellationToken.None);

        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
    }

    [Fact]
    public async Task Unlock_reports_that_sap_has_no_such_account()
    {
        var result = await UnlockHandler(new RecordingSapClient([]))
            .Handle(new UnlockSapUserAccountCommand(404), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapUser.NotFound", result.FirstError.Code);
    }

    /// <summary>
    /// SAP's own sentence names the authorisation the Service Layer account is missing, which is
    /// what an administrator has to act on. Restating it as "the unlock failed" loses the whole
    /// answer.
    /// </summary>
    [Fact]
    public async Task Unlock_passes_sap_refusal_through_and_audits_the_failure()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: true)])
        {
            LockWriteRefusal = "No authorization to update users"
        };
        var audit = new RecordingAuditService();

        var result = await UnlockHandler(sap, audit).Handle(new UnlockSapUserAccountCommand(4), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapUser.Rejected", result.FirstError.Code);
        Assert.Equal("No authorization to update users", result.FirstError.Description);

        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Succeeded);
    }

    /// <summary>
    /// Production, 2026-09-21: SAP refused to unlock a manager because one of their user settings
    /// differed from their User Defaults. SAP's sentence names the setting but not the fix, and
    /// the setting is not writable through the Service Layer, so the error says where to fix it.
    /// </summary>
    [Fact]
    public async Task Unlock_refused_over_user_defaults_says_where_to_fix_it()
    {
        var sap = new RecordingSapClient([Account(4, "jason", "Jason", locked: true)])
        {
            LockWriteRefusal = "Checkbox \"Take Control of eDoc Processing in Electronic Document Monitor\" in \"Users - Setup\" is different to \"User Defaults\" "
        };
        var audit = new RecordingAuditService();

        var result = await UnlockHandler(sap, audit).Handle(new UnlockSapUserAccountCommand(4), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapUser.DefaultsMismatch", result.FirstError.Code);
        Assert.Contains("\"Take Control of eDoc Processing in Electronic Document Monitor\"", result.FirstError.Description);
        Assert.Contains("Users - Setup", result.FirstError.Description);
        Assert.False(Assert.Single(audit.Entries).Succeeded);
    }

    /// <summary>
    /// Production, 2026-09-22: SAP refused to unlock Dispatch with a bare "Internal error (-5002)
    /// occurred" and empty details. That sentence gives the operator nothing to act on, so the
    /// error sends them to the SAP client, which runs the same check and names what it objects to.
    /// </summary>
    [Fact]
    public async Task Unlock_refused_without_a_reason_sends_the_operator_to_the_sap_client()
    {
        var sap = new RecordingSapClient([Account(53, "Dispatch", "Kefalos Dispatch", locked: true)])
        {
            LockWriteRefusal = "Internal error (-5002) occurred"
        };
        var audit = new RecordingAuditService();

        var result = await UnlockHandler(sap, audit).Handle(new UnlockSapUserAccountCommand(53), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapUser.Unexplained", result.FirstError.Code);
        Assert.Contains("Users - Setup", result.FirstError.Description);
        Assert.Contains("Internal error (-5002) occurred", result.FirstError.Description);
        Assert.Equal("Internal error (-5002) occurred", Assert.Single(audit.Entries).ErrorMessage);
    }

    /// <summary>
    /// The Web sends its integration key alongside the signed-in user's token, and the key's
    /// identity comes first — so the audit row is signed from the account, not from the name claim.
    /// </summary>
    [Fact]
    public async Task Unlock_audits_the_signed_in_account_not_the_integration_key()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: true)]);
        var audit = new RecordingAuditService();

        var handler = UnlockHandler(
            sap,
            audit,
            principal: PrincipalWithKeyNameClaim("MainIntegration", AdminUserId),
            caller: new CallerAccount(AdminUserId, "tmurombe", "Admin"));

        await handler.Handle(new UnlockSapUserAccountCommand(4), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Contains("tmurombe", entry.Details);
        Assert.DoesNotContain("MainIntegration", entry.Details);
    }

    #endregion

    #region Change password

    [Fact]
    public async Task Password_change_sends_the_new_password_to_sap_and_answers_with_the_account()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: false)]);

        var result = await PasswordHandler(sap)
            .Handle(new ChangeSapUserPasswordCommand(4, "Ch33se!2026"), CancellationToken.None);

        Assert.False(result.IsError, Describe(result.Errors));
        Assert.Equal((4, "Ch33se!2026"), sap.PasswordWrite);
        Assert.Equal("kmoyo", result.Value.UserCode);
    }

    /// <summary>
    /// The password reaches SAP and nothing else. An audit row that carried it would put it in a
    /// table people read, and one that carried its length would narrow it.
    /// </summary>
    [Fact]
    public async Task Password_change_never_puts_the_password_in_the_audit_row()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: false)]);
        var audit = new RecordingAuditService();

        await PasswordHandler(sap, audit)
            .Handle(new ChangeSapUserPasswordCommand(4, "Ch33se!2026"), CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.ChangeSapUserPassword, entry.Action);
        Assert.True(entry.Succeeded);

        var written = string.Join(" ", entry.Action, entry.EntityType, entry.EntityId, entry.Details, entry.ErrorMessage);
        Assert.DoesNotContain("Ch33se!2026", written);
    }

    /// <summary>
    /// SAP's password policy is the authority and it varies by company. Its refusal names the rule
    /// that was broken, which is the only form of the message anybody can act on.
    /// </summary>
    [Fact]
    public async Task Password_change_passes_sap_policy_refusal_through()
    {
        var sap = new RecordingSapClient([Account(4, "kmoyo", "Kudzai Moyo", locked: false)])
        {
            PasswordWriteRefusal = "Password must contain at least one digit"
        };
        var audit = new RecordingAuditService();

        var result = await PasswordHandler(sap, audit)
            .Handle(new ChangeSapUserPasswordCommand(4, "cheeseplease"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Password must contain at least one digit", result.FirstError.Description);

        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Succeeded);
        Assert.Equal("Password must contain at least one digit", entry.ErrorMessage);
        Assert.DoesNotContain("cheeseplease", entry.Details ?? string.Empty);
    }

    [Fact]
    public async Task Password_change_reports_that_sap_has_no_such_account()
    {
        var result = await PasswordHandler(new RecordingSapClient([]))
            .Handle(new ChangeSapUserPasswordCommand(404, "Ch33se!2026"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("SapUser.NotFound", result.FirstError.Code);
    }

    #endregion

    #region The password validator

    [Theory]
    [InlineData("")]
    [InlineData("short1!")]
    [InlineData(" Ch33se!2026")]
    [InlineData("Ch33se!2026 ")]
    public void Validator_refuses_a_password_sap_would_not_take(string password)
    {
        var result = new ChangeSapUserPasswordValidator()
            .Validate(new ChangeSapUserPasswordCommand(4, password));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validator_refuses_a_password_longer_than_sap_stores()
    {
        var tooLong = new string('a', ChangeSapUserPasswordValidator.MaxPasswordLength + 1);

        var result = new ChangeSapUserPasswordValidator()
            .Validate(new ChangeSapUserPasswordCommand(4, tooLong));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validator_accepts_a_password_within_the_bounds()
    {
        var result = new ChangeSapUserPasswordValidator()
            .Validate(new ChangeSapUserPasswordCommand(4, "Ch33se!2026"));

        Assert.True(result.IsValid);
    }

    #endregion

    #region Harness

    private static string Describe(List<Error> errors) =>
        string.Join("; ", errors.Select(error => error.Description));

    private static SAPUserAccount Account(
        int internalKey,
        string userCode,
        string userName,
        bool locked,
        bool superuser = false) => new()
        {
            InternalKey = internalKey,
            UserCode = userCode,
            UserName = userName,
            EMail = $"{userCode}@example.com",
            Locked = locked ? SapYesNo.Yes : SapYesNo.No,
            Superuser = superuser ? SapYesNo.Yes : SapYesNo.No,
            LastPasswordChangedBy = "manager"
        };

    private static GetSapUserAccountsHandler ListHandler(RecordingSapClient sap, bool sapEnabled = true) =>
        new(sap.AsClient(), Settings(sapEnabled), NullLogger<GetSapUserAccountsHandler>.Instance);

    private static UnlockSapUserAccountHandler UnlockHandler(
        RecordingSapClient sap,
        RecordingAuditService? audit = null,
        ClaimsPrincipal? principal = null,
        CallerAccount? caller = null) =>
        new(sap.AsClient(),
            audit ?? new RecordingAuditService(),
            new FixedCallerAccountReader(caller ?? new CallerAccount(AdminUserId, "tmurombe", "Admin")),
            HttpContext(principal),
            Settings(true),
            NullLogger<UnlockSapUserAccountHandler>.Instance);

    private static ChangeSapUserPasswordHandler PasswordHandler(
        RecordingSapClient sap,
        RecordingAuditService? audit = null) =>
        new(sap.AsClient(),
            audit ?? new RecordingAuditService(),
            new FixedCallerAccountReader(new CallerAccount(AdminUserId, "tmurombe", "Admin")),
            HttpContext(null),
            Settings(true),
            NullLogger<ChangeSapUserPasswordHandler>.Instance);

    private static IOptions<SAPSettings> Settings(bool enabled) =>
        Options.Create(new SAPSettings { Enabled = enabled });

    private static IHttpContextAccessor HttpContext(ClaimsPrincipal? principal)
    {
        var context = new DefaultHttpContext();
        if (principal is not null)
        {
            context.User = principal;
        }

        return new HttpContextAccessor { HttpContext = context };
    }

    /// <summary>
    /// The shape a Web request really arrives in: the integration key's identity first, the user's
    /// second. Asking the merged principal for a name answers "MainIntegration".
    /// </summary>
    private static ClaimsPrincipal PrincipalWithKeyNameClaim(string keyName, Guid userId) =>
        new(
        [
            new ClaimsIdentity([new Claim(ClaimTypes.Name, keyName)], "ApiKey"),
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Bearer")
        ]);

    private sealed class FixedCallerAccountReader(CallerAccount account) : ICallerAccountReader
    {
        public Task<ErrorOr<CallerAccount>> ReadAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken)
            => Task.FromResult<ErrorOr<CallerAccount>>(account);
    }

    /// <summary>
    /// SAP, answering from a dictionary and remembering the writes. The lock write is applied to the
    /// stored account unless <see cref="IgnoreLockWrites"/> says otherwise — which is how a SAP that
    /// accepts a PATCH and changes nothing is modelled.
    /// </summary>
    private sealed class RecordingSapClient(List<SAPUserAccount> accounts)
    {
        public string? RequestedSearch { get; private set; }
        public bool RequestedLockedOnly { get; private set; }

        public (int InternalKey, bool Locked)? LockWrite { get; private set; }
        public (int InternalKey, string Password)? PasswordWrite { get; private set; }

        /// <summary>SAP accepts the lock write but leaves the account as it was.</summary>
        public bool IgnoreLockWrites { get; init; }

        public string? LockWriteRefusal { get; init; }
        public string? PasswordWriteRefusal { get; init; }

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetSapUserAccountsAsync) => List(args!),
            nameof(ISAPServiceLayerClient.GetSapUserAccountAsync) => One(args!),
            nameof(ISAPServiceLayerClient.SetSapUserLockedAsync) => SetLocked(args!),
            nameof(ISAPServiceLayerClient.ChangeSapUserPasswordAsync) => SetPassword(args!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private Task<List<SAPUserAccount>> List(object?[] args)
        {
            RequestedSearch = (string?)args[0];
            RequestedLockedOnly = (bool)args[1]!;

            var rows = accounts.AsEnumerable();
            if (RequestedLockedOnly)
            {
                rows = rows.Where(account => account.IsLocked);
            }

            return Task.FromResult(rows.ToList());
        }

        private Task<SAPUserAccount?> One(object?[] args)
        {
            var key = (int)args[0]!;
            return Task.FromResult(accounts.FirstOrDefault(account => account.InternalKey == key));
        }

        private Task SetLocked(object?[] args)
        {
            var key = (int)args[0]!;
            var locked = (bool)args[1]!;

            if (LockWriteRefusal is not null)
            {
                throw new SapRequestRejectedException(
                    $"unlock SAP user account {key}", HttpStatusCode.BadRequest, LockWriteRefusal);
            }

            LockWrite = (key, locked);

            if (!IgnoreLockWrites)
            {
                var account = accounts.FirstOrDefault(row => row.InternalKey == key);
                if (account is not null)
                {
                    account.Locked = locked ? SapYesNo.Yes : SapYesNo.No;
                }
            }

            return Task.CompletedTask;
        }

        private Task SetPassword(object?[] args)
        {
            var key = (int)args[0]!;
            var password = (string)args[1]!;

            if (PasswordWriteRefusal is not null)
            {
                throw new SapRequestRejectedException(
                    $"change the password of SAP user account {key}",
                    HttpStatusCode.BadRequest,
                    PasswordWriteRefusal);
            }

            PasswordWrite = (key, password);
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEntry(
        string Action,
        string? EntityType,
        string? EntityId,
        string? Details,
        bool Succeeded,
        string? ErrorMessage);

    private sealed class RecordingAuditService : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(
            string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null)
        {
            Entries.Add(new AuditEntry(action, entityType, entityId, details, isSuccess, errorMessage));
            return Task.CompletedTask;
        }

        public Task LogAsync(string action, string? entityType = null, string? entityId = null)
        {
            Entries.Add(new AuditEntry(action, entityType, entityId, null, true, null));
            return Task.CompletedTask;
        }

        public Task LogAsync(
            string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null)
        {
            Entries.Add(new AuditEntry(action, entityType, entityId, details, isSuccess, errorMessage));
            return Task.CompletedTask;
        }
    }

    #endregion
}
