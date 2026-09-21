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

namespace ShopInventory.Features.SapUsers.Commands.ChangeSapUserPassword;

/// <summary>
/// Sets a SAP user account's password.
/// </summary>
/// <remarks>
/// SAP's password policy is enforced by SAP, and its refusal is passed through word for word: it
/// names the rule that was broken ("Password must contain at least one digit"), which is the only
/// form of the message an administrator can act on.
/// <para>
/// Nothing about the password is logged, audited or answered back — not its length, not a hash, not
/// a masked form. The audit row records that the password of a named account was changed and by
/// whom, which is what an audit of this action is for.
/// </para>
/// </remarks>
public sealed class ChangeSapUserPasswordHandler(
    ISAPServiceLayerClient sap,
    IAuditService auditService,
    ICallerAccountReader callerAccounts,
    IHttpContextAccessor httpContextAccessor,
    IOptions<SAPSettings> sapSettings,
    ILogger<ChangeSapUserPasswordHandler> logger)
    : IRequestHandler<ChangeSapUserPasswordCommand, ErrorOr<SapUserAccountDto>>
{
    public async Task<ErrorOr<SapUserAccountDto>> Handle(
        ChangeSapUserPasswordCommand command,
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

        // Resolved before the write so a refusal is audited against the person, not the key.
        var actor = await SapUserAuditActor.ResolveAsync(callerAccounts, httpContextAccessor, cancellationToken);

        try
        {
            await sap.ChangeSapUserPasswordAsync(command.InternalKey, command.NewPassword, cancellationToken);
        }
        catch (SapRequestRejectedException ex)
        {
            logger.LogWarning(
                ex,
                "SAP refused the password change for user account {InternalKey} ({UserCode})",
                command.InternalKey,
                account.UserCode);
            await LogAuditAsync(command, account, actor, succeeded: false, ex.SapMessage);
            return Errors.SapUser.Rejected(ex.SapMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to change the password of SAP user account {InternalKey}", command.InternalKey);
            await LogAuditAsync(command, account, actor, succeeded: false, ex.Message);
            return Errors.SapUser.Unreachable;
        }

        logger.LogInformation(
            "Changed the password of SAP user account {InternalKey} ({UserCode}) for {Actor}",
            command.InternalKey,
            account.UserCode,
            actor);

        await LogAuditAsync(command, account, actor, succeeded: true, errorMessage: null);

        // Read back so the answer carries what SAP now records — LastPasswordChangedBy in
        // particular, which is the account's own evidence that the change landed. A read that
        // fails costs the caller that evidence, not the change.
        var updated = await ReadBackAsync(command.InternalKey, account, cancellationToken);
        return SapUserAccountMapping.ToDto(updated);
    }

    private async Task<SAPUserAccount> ReadBackAsync(
        int internalKey,
        SAPUserAccount before,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sap.GetSapUserAccountAsync(internalKey, cancellationToken) ?? before;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "The password of SAP user account {InternalKey} was changed but it could not be read back", internalKey);
            return before;
        }
    }

    private async Task LogAuditAsync(
        ChangeSapUserPasswordCommand command,
        SAPUserAccount account,
        string actor,
        bool succeeded,
        string? errorMessage)
    {
        var details = succeeded
            ? $"Password of SAP user '{account.UserCode}' (InternalKey {command.InternalKey}) changed by {actor}"
            : $"Password of SAP user '{account.UserCode}' (InternalKey {command.InternalKey}) could not be changed by {actor}";

        try
        {
            await auditService.LogAsync(
                AuditActions.ChangeSapUserPassword,
                "SapUser",
                command.InternalKey.ToString(),
                details,
                succeeded,
                errorMessage);
        }
        catch (Exception ex)
        {
            // An audit row that will not write must not undo a password that has already changed.
            logger.LogWarning(
                ex, "Failed to audit the password change of SAP user account {InternalKey}", command.InternalKey);
        }
    }
}
