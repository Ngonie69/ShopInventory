using ErrorOr;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCustomerAuth;

/// <summary>
/// Turns a verified customer account into a session: an access token, a stored refresh token, and
/// the profile the app shows.
/// </summary>
/// <remarks>
/// Shared by sign-in and by refresh because both must produce a session the same way. Duplicating
/// it would be how the two drift — one growing a check or a claim the other lacks — and a session
/// issued by the weaker path would be indistinguishable afterwards from one issued by the stronger.
/// </remarks>
public interface IVanSalesCustomerSessionIssuer
{
    /// <summary>
    /// Issue a session for an account, writing the refresh token to the store.
    /// </summary>
    /// <remarks>
    /// <c>replacesTokenHash</c> carries the refresh token being rotated out, and is null for a
    /// fresh sign-in. Supplying it is what links the outgoing token to its successor, which is how
    /// a rotated token is later told apart from a revoked one.
    /// </remarks>
    Task<ErrorOr<VanSalesCustomerSessionResult>> IssueAsync(
        int accountId,
        string? deviceId,
        string? deviceName,
        string? ipAddress,
        string? replacesTokenHash,
        CancellationToken cancellationToken);
}
