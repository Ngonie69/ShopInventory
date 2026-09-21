using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.SapUsers.Commands.UnlockSapUserAccount;

/// <summary>
/// Clears the lock SAP is keeping a user out with, then reads the account back so the caller is
/// shown what SAP now holds rather than what this handler assumed it would.
/// </summary>
/// <remarks>
/// The read-back is the point of the whole handler over a bare PATCH. SAP accepts a PATCH that
/// changes nothing, so "204 No Content" is not evidence the account can sign in — and an unlock is
/// exactly the operation somebody walks away from believing it worked.
/// <para>
/// An account that is already unlocked is refused rather than PATCHed. It is a different situation
/// from the one the operator thinks they are in: whoever reported being locked out is failing on
/// something else, and quietly answering "done" would send them round the same loop.
/// </para>
/// </remarks>
public sealed class UnlockSapUserAccountHandler(
    ISAPServiceLayerClient sap,
    IAuditService auditService,
    ICallerAccountReader callerAccounts,
    IHttpContextAccessor httpContextAccessor,
    IOptions<SAPSettings> sapSettings,
    ILogger<UnlockSapUserAccountHandler> logger)
    : IRequestHandler<UnlockSapUserAccountCommand, ErrorOr<SapUserAccountDto>>
{
    public async Task<ErrorOr<SapUserAccountDto>> Handle(
        UnlockSapUserAccountCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.SapUser.SapDisabled;
        }

        SAPUserAccount? account;
        try
        {
            account = await sap.GetSapUserAccountAsync(command.InternalKey, cancellationToken);
        }
        catch (SapRequestRejectedException ex)
        {
            logger.LogWarning(ex, "SAP refused the read of user account {InternalKey}", command.InternalKey);
            return Errors.SapUser.Rejected(ex.SapMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to read SAP user account {InternalKey}", command.InternalKey);
            return Errors.SapUser.Unreachable;
        }

        if (account is null)
        {
            return Errors.SapUser.NotFound(command.InternalKey);
        }

        if (!account.IsLocked)
        {
            return Errors.SapUser.NotLocked(account.UserCode ?? command.InternalKey.ToString());
        }

        // Resolved before the write so a refusal is audited against the person, not the key.
        var actor = await SapUserAuditActor.ResolveAsync(callerAccounts, httpContextAccessor, cancellationToken);

        try
        {
            await sap.SetSapUserLockedAsync(command.InternalKey, locked: false, cancellationToken);
        }
        catch (SapRequestRejectedException ex)
        {
            logger.LogWarning(
                ex, "SAP refused to unlock user account {InternalKey} ({UserCode})", command.InternalKey, account.UserCode);
            await LogAuditAsync(command, account, actor, succeeded: false, ex.SapMessage);
            return Errors.SapUser.Rejected(ex.SapMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to unlock SAP user account {InternalKey}", command.InternalKey);
            await LogAuditAsync(command, account, actor, succeeded: false, ex.Message);
            return Errors.SapUser.Unreachable;
        }

        // What SAP holds now, not what the PATCH was told to set.
        var updated = await ReadBackAsync(command.InternalKey, account, cancellationToken);

        logger.LogInformation(
            "Unlocked SAP user account {InternalKey} ({UserCode}) for {Actor}",
            command.InternalKey,
            updated.UserCode,
            actor);

        await LogAuditAsync(command, updated, actor, succeeded: true, errorMessage: null);

        return SapUserAccountMapping.ToDto(updated);
    }

    /// <summary>
    /// The account as SAP holds it after the write. A read that fails here is not the operation
    /// failing — the unlock has already been accepted — so the account that went in is answered
    /// with its lock cleared rather than the whole call being turned into an error.
    /// </summary>
    private async Task<SAPUserAccount> ReadBackAsync(
        int internalKey,
        SAPUserAccount before,
        CancellationToken cancellationToken)
    {
        try
        {
            var after = await sap.GetSapUserAccountAsync(internalKey, cancellationToken);
            if (after is not null)
            {
                return after;
            }

            logger.LogWarning("SAP user account {InternalKey} was unlocked but could not be read back", internalKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SAP user account {InternalKey} was unlocked but could not be read back", internalKey);
        }

        before.Locked = SapYesNo.No;
        return before;
    }

    private async Task LogAuditAsync(
        UnlockSapUserAccountCommand command,
        SAPUserAccount account,
        string actor,
        bool succeeded,
        string? errorMessage)
    {
        var details = succeeded
            ? $"SAP user '{account.UserCode}' (InternalKey {command.InternalKey}) unlocked by {actor}"
            : $"SAP user '{account.UserCode}' (InternalKey {command.InternalKey}) could not be unlocked by {actor}";

        try
        {
            await auditService.LogAsync(
                AuditActions.UnlockSapUser,
                "SapUser",
                command.InternalKey.ToString(),
                details,
                succeeded,
                errorMessage);
        }
        catch (Exception ex)
        {
            // An audit row that will not write must not undo an unlock that already happened.
            logger.LogWarning(ex, "Failed to audit the unlock of SAP user account {InternalKey}", command.InternalKey);
        }
    }
}
