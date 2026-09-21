using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.SapUsers.Commands.ChangeSapUserPassword;
using ShopInventory.Features.SapUsers.Commands.UnlockSapUserAccount;
using ShopInventory.Features.SapUsers.Queries.GetSapUserAccounts;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// SAP Business One user accounts: who the company has, which of them SAP is keeping out, and the
/// two things an administrator does about it — clear the lock, and set a password.
/// </summary>
/// <remarks>
/// These are accounts in SAP, not accounts in this application. The application's own users are
/// under <c>/api/usermanagement</c> and the two are unrelated: a person may hold one, the other,
/// both or neither, and nothing here creates, deletes or re-roles a SAP account. Creating one is a
/// licensing decision and stays in the B1 client.
/// <para>
/// Every route is permission-gated separately. Reading who is locked out is a far smaller thing
/// than setting the password of an account that can post to the ledger, and an organisation that
/// wants a help desk to do the first without the second can say so.
/// </para>
/// <para>
/// The Service Layer account this application signs in as has to be a SAP superuser for the two
/// writes; SAP refuses them otherwise, and its refusal reaches the caller unchanged.
/// </para>
/// </remarks>
[Route("api/sap-users")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class SapUserController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The company's SAP user accounts, ordered by user code, with the count of those locked out.
    /// </summary>
    /// <param name="search">Matched against both the user code and the name; omit to read all.</param>
    /// <param name="lockedOnly">Only the accounts SAP is currently keeping out.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    [HttpGet]
    [RequirePermission(Permission.ViewSapUsers)]
    [ProducesResponseType(typeof(SapUserAccountListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? search = null,
        [FromQuery] bool lockedOnly = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetSapUserAccountsQuery(search, lockedOnly), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Clears SAP's lock on one account, so it can sign in again.
    /// </summary>
    /// <remarks>
    /// The account is read back from SAP after the write, and the answer is what SAP then holds —
    /// so a 200 here means the account really is unlocked, not that SAP accepted a PATCH. An
    /// account that was not locked to begin with is refused rather than silently confirmed.
    /// </remarks>
    [HttpPost("{internalKey:int}/unlock")]
    [RequirePermission(Permission.UnlockSapUsers)]
    [ProducesResponseType(typeof(SapUserAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlock(int internalKey, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnlockSapUserAccountCommand(internalKey), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Sets a new password on a SAP user account.
    /// </summary>
    /// <remarks>
    /// SAP's own password policy decides what it will accept; when it refuses, its reason is what
    /// comes back. The password is never logged, never audited and never echoed — the answer is the
    /// account, with the <c>lastPasswordChangedBy</c> SAP now records against it.
    /// </remarks>
    [HttpPost("{internalKey:int}/password")]
    [RequirePermission(Permission.ChangeSapUserPasswords)]
    [ProducesResponseType(typeof(SapUserAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangePassword(
        int internalKey,
        [FromBody] ChangeSapUserPasswordRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new ChangeSapUserPasswordCommand(internalKey, request.NewPassword), cancellationToken);
        return result.Match(Ok, Problem);
    }
}
