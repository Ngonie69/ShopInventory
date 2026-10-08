using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.CustomerDocuments;

/// <summary>The name a person's actions on customer documents are recorded under.</summary>
internal static class CustomerDocumentActor
{
    /// <summary>"First Last", else the username; null for an account that does not exist or is disabled.</summary>
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
