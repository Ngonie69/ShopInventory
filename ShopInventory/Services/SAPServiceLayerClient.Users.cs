using System.Text.Json.Nodes;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The SAP user administration surface: reading the company's user accounts, clearing the lock that
/// keeps one out, and setting a password for one.
/// </summary>
/// <remarks>
/// A separate file rather than more of <c>SAPServiceLayerClient.cs</c> for the reason
/// <c>SAPServiceLayerClient.Approvals.cs</c> is one — nothing here shares a code path with the
/// document reads there. It borrows that file's <c>SendSapRequestAsync</c> plumbing, which carries
/// the session cookie, retries transient failures and re-authenticates once on a 401.
///
/// <para>
/// <b>Both writes are PATCHes against <c>Users(InternalKey)</c>.</b> SAP's <c>Locked</c> is the same
/// flag whether an administrator ticked it in User Setup or the Service Layer's own failed-sign-in
/// count set it, so clearing it is the unlock in both cases. <c>UserPassword</c> is write-only: it
/// is never in a <c>$select</c> here and never in a log line.
/// </para>
/// <para>
/// <b>The session user has to be a superuser.</b> SAP refuses a write to <c>Users</c> from an
/// account that is not, with a message naming the authorisation rather than the field, and that
/// message is passed through to the caller unchanged rather than translated — an administrator who
/// sees it needs to act in SAP, not here.
/// </para>
/// </remarks>
public partial class SAPServiceLayerClient
{
    /// <summary>
    /// What the administration screen reads. <c>UserPassword</c> is deliberately absent: SAP would
    /// serve it as a masked placeholder, and a field nothing can use is a field that invites being
    /// shown.
    /// </summary>
    private const string SapUserAccountSelect =
        "$select=InternalKey,UserCode,UserName,eMail,Locked,Superuser,LastPasswordChangedBy,LastLogoutDate";

    #region Reads

    /// <inheritdoc cref="ISAPServiceLayerClient.GetSapUserAccountsAsync" />
    public async Task<List<SAPUserAccount>> GetSapUserAccountsAsync(
        string? search = null,
        bool lockedOnly = false,
        CancellationToken cancellationToken = default)
    {
        var clauses = new List<string>();

        if (lockedOnly)
        {
            clauses.Add($"Locked eq '{SapYesNo.Yes}'");
        }

        // Matched on both the login code and the person's name: an administrator looking somebody up
        // has one or the other written down, not reliably the one SAP keys by.
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var safe = EscapeODataStringLiteral(term);
            clauses.Add($"(contains(UserCode,'{safe}') or contains(UserName,'{safe}'))");
        }

        var filter = clauses.Count == 0
            ? string.Empty
            : $"$filter={Uri.EscapeDataString(string.Join(" and ", clauses))}&";

        var url = $"Users?{filter}{SapUserAccountSelect}&$orderby=UserCode&$top={SapUserAccountLimits.PageSize}";

        var page = await ReadSapJsonAsync<SAPResponse<SAPUserAccount>>(
            url,
            lockedOnly ? "read the locked SAP user accounts" : "read the SAP user accounts",
            cancellationToken,
            pageSize: SapUserAccountLimits.PageSize);

        return page?.Value ?? [];
    }

    /// <inheritdoc cref="ISAPServiceLayerClient.GetSapUserAccountAsync" />
    public Task<SAPUserAccount?> GetSapUserAccountAsync(int internalKey, CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<SAPUserAccount>(
            $"Users({internalKey})?{SapUserAccountSelect}",
            $"read SAP user account {internalKey}",
            cancellationToken);

    #endregion

    #region Writes

    /// <inheritdoc cref="ISAPServiceLayerClient.SetSapUserLockedAsync" />
    public async Task SetSapUserLockedAsync(int internalKey, bool locked, CancellationToken cancellationToken = default)
    {
        var json = new JsonObject
        {
            ["Locked"] = locked ? SapYesNo.Yes : SapYesNo.No
        }.ToJsonString();

        var operation = locked
            ? $"lock SAP user account {internalKey}"
            : $"unlock SAP user account {internalKey}";

        _logger.LogInformation(
            "Setting SAP user account {InternalKey} Locked to {Locked}", internalKey, locked ? SapYesNo.Yes : SapYesNo.No);

        using var response = await SendSapRequestAsync(
            () => CreateSapJsonRequest(HttpMethod.Patch, $"Users({internalKey})", json),
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        await EnsureSapSuccessAsync(response, operation, cancellationToken);
    }

    /// <inheritdoc cref="ISAPServiceLayerClient.ChangeSapUserPasswordAsync" />
    public async Task ChangeSapUserPasswordAsync(
        int internalKey,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            throw new ArgumentException("A SAP password cannot be empty.", nameof(newPassword));
        }

        var json = new JsonObject
        {
            ["UserPassword"] = newPassword
        }.ToJsonString();

        var operation = $"change the password of SAP user account {internalKey}";

        // The payload is never logged, and the key is all this line needs to be useful.
        _logger.LogInformation("Changing the password of SAP user account {InternalKey}", internalKey);

        using var response = await SendSapRequestAsync(
            () => CreateSapJsonRequest(HttpMethod.Patch, $"Users({internalKey})", json),
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        await EnsureSapSuccessAsync(response, operation, cancellationToken);
    }

    #endregion
}
