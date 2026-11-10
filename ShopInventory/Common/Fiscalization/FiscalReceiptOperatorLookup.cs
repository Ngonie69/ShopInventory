using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// The ShopInventory user who raised a document, as the operator the fiscalisation platform names for its receipt.
/// </summary>
/// <remarks>
/// One lookup for every route that raises a document here and fiscalises it: the invoice queue, the credit note
/// create and the approval add. Call it only for a document ShopInventory raised (see
/// <see cref="FiscalReceiptOperator"/>). The name is a courtesy: a lookup that fails names the username alone, and
/// with neither an id nor a username it names nobody, so the platform falls back to the SAP user who created the
/// document. It never fails the fiscalisation it serves.
/// </remarks>
internal static class FiscalReceiptOperatorLookup
{
    public static async Task<FiscalReceiptOperator?> ResolveAsync(
        ApplicationDbContext dbContext,
        Guid? userId,
        string? username,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(username) ? null : username.Trim();

        try
        {
            var users = dbContext.Users.AsNoTracking();
            var user = userId.HasValue && userId.Value != Guid.Empty
                ? await users
                    .Where(candidate => candidate.Id == userId.Value)
                    .Select(candidate => new { candidate.Username, candidate.FirstName, candidate.LastName })
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            if (user is null && name is not null)
            {
                user = await users
                    .Where(candidate => candidate.Username == name)
                    .Select(candidate => new { candidate.Username, candidate.FirstName, candidate.LastName })
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (user is not null)
            {
                return FiscalReceiptOperator.For(user.Username, user.FirstName, user.LastName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read who raised the document (user {UserId}); naming {Username} alone", userId, name);
        }

        return FiscalReceiptOperator.For(name);
    }
}
