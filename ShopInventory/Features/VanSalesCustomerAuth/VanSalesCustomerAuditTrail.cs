using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>
/// One audit row, described before it is written.
/// </summary>
/// <param name="Action">One of the <c>VanSalesCustomer*</c> constants on <see cref="AuditActions"/>.</param>
/// <param name="MaskedPhone">
/// The number the request came in under, masked. Null on the paths that identify the caller by their
/// token rather than their phone.
/// </param>
/// <param name="AccountId">The account, where the attempt reached one. Null otherwise.</param>
/// <param name="Details">What happened, in a sentence.</param>
/// <param name="Success">Whether the attempt did what the caller asked.</param>
/// <param name="Error">Why not, when it did not.</param>
public readonly record struct VanSalesCustomerAuditRow(
    string Action,
    string? MaskedPhone,
    int? AccountId,
    string Details,
    bool Success,
    string? Error = null);

/// <summary>
/// Writes the customer app's authentication events to the audit trail.
/// </summary>
/// <remarks>
/// <para>
/// Staff sign-ins have been audited all along — a wrong password writes a <c>LoginFailed</c> row
/// naming the user. The customer app had nothing: an account that can place orders in a shop's name
/// could be signed into, refused, code-bombed or handed a fresh token, and only the application log
/// knew. Deciding who gets one of these accounts was already audited, which made the silence around
/// using one the odder half.
/// </para>
/// <para>
/// The identity is supplied rather than read from the request, because four of these five endpoints
/// are <c>[AllowAnonymous]</c> — there is no principal to resolve, and the overload that resolves one
/// would file every row under "Anonymous".
/// </para>
/// <para>
/// The phone number is recorded masked, following what these handlers already do in their logs. The
/// account id is the join key, and it is what
/// <c>GET /api/UserActivity/entity/VanSalesCustomerAccount/{id}</c> reads.
/// </para>
/// <para>
/// <b>Call this exactly once on every path, including the ones that fail early.</b> These handlers go
/// to some length to make a registered number and an unregistered one cost the same wall-clock time,
/// and an audit write on only one of the two branches would hand back through the clock precisely the
/// answer the uniform error is refusing to give.
/// </para>
/// </remarks>
public static class VanSalesCustomerAuditTrail
{
    public static Task RecordAsync(IAuditService auditService, VanSalesCustomerAuditRow row) =>
        auditService.LogAsync(
            row.Action,
            row.MaskedPhone ?? (row.AccountId is { } id ? $"vsc:{id}" : "vsc:unknown"),
            ApplicationRoles.VanSalesCustomer,
            "VanSalesCustomerAccount",
            row.AccountId?.ToString(),
            row.Details,
            endpoint: null,
            row.Success,
            row.Error);
}
