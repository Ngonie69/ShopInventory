using System.Security.Claims;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
// Aliased: inside ShopInventory.Common.Security a bare "Errors" names the sibling namespace, not the class.
using AppErrors = ShopInventory.Common.Errors.Errors;

namespace ShopInventory.Common.Security;

/// <summary>
/// The person behind a request, as their account records them. <see cref="UserId"/> is null for a
/// service caller: an integration key on its own, which names no account.
/// </summary>
public sealed record CallerAccount(Guid? UserId, string? Username, string? Role)
{
    public static readonly CallerAccount ServiceCaller = new(null, null, null);

    public bool IsServiceCaller => UserId is null;

    public bool IsInRole(string role) =>
        Role is not null && string.Equals(Role.Trim(), role, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Answers "who is asking, and in what role" from the account in the database, never from the role
/// claims on the request.
/// </summary>
/// <remarks>
/// ShopInventory.Web calls the API with its integration key and the signed-in user's token together.
/// The ApiAccess policies authenticate both schemes, so the principal carries two identities and the
/// key's comes first: asked of the whole principal, <c>IsInRole("Admin")</c> is true for every Web
/// user and <c>FindFirst(ClaimTypes.Name)</c> is the key's name. A merchandiser signed in on the Web
/// skipped their own-timesheets scope because of it. Role claims on the user's own identity would be
/// right today, but only until a token outlives a role change; the account row is right always.
/// </remarks>
public interface ICallerAccountReader
{
    /// <summary>
    /// <see cref="CallerAccount.ServiceCaller"/> when the request names no user. An account that is
    /// gone or disabled is <c>Auth.UserNotFound</c>, not a service caller: the key riding on the same
    /// request would otherwise carry it past every scope.
    /// </summary>
    Task<ErrorOr<CallerAccount>> ReadAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken);
}

public sealed class CallerAccountReader(ApplicationDbContext context) : ICallerAccountReader
{
    public async Task<ErrorOr<CallerAccount>> ReadAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(principal);
        if (userId is null)
        {
            return CallerAccount.ServiceCaller;
        }

        var account = await context.Users.AsNoTracking()
            .Where(user => user.Id == userId.Value && user.IsActive)
            .Select(user => new { user.Id, user.Username, user.Role })
            .SingleOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            return AppErrors.Auth.UserNotFound;
        }

        return new CallerAccount(account.Id, account.Username, account.Role);
    }
}
