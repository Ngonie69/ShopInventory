using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.MarketBreakages;

/// <summary>
/// The name a decision is recorded under, read from the account.
/// </summary>
/// <remarks>
/// Not <c>User.Identity.Name</c>: the Web calls with its integration key alongside the user's token,
/// and the key's identity comes first, so that name is the key's for every Web user.
/// </remarks>
public static class MarketBreakageActor
{
    public static async Task<string?> ResolveNameAsync(
        ApplicationDbContext context,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .Where(account => account.Id == userId && account.IsActive)
            .Select(account => new { account.FirstName, account.LastName, account.Username })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null)
            return null;

        var fullName = string.Join(" ", new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return string.IsNullOrWhiteSpace(fullName) ? user.Username : fullName;
    }
}
