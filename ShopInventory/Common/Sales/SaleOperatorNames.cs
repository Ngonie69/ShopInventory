using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Turns the account id a sale was captured under into the name of a person.
/// </summary>
/// <remarks>
/// <para>
/// <c>DesktopSaleEntity.CreatedBy</c> holds <c>account.UserId.ToString()</c> — every writer of that
/// column stores an id, never a name. Shown raw it is a GUID, which tells a reader nothing about who
/// rang the sale up, so every surface that states "captured by" has to resolve it first.
/// </para>
/// <para>
/// One resolver rather than one per reader: the SAP remark, the operator breakdown on the sales
/// analysis and the console drawer all answer the same question, and three copies of the lookup are
/// three chances for the same sale to be attributed to three different names.
/// </para>
/// </remarks>
public static class SaleOperatorNames
{
    /// <summary>
    /// A person's name as it should be shown, falling back to the account they sign in as.
    /// </summary>
    public static string Format(string? firstName, string? lastName, string username)
    {
        var full = string.Join(
            " ",
            new[] { firstName, lastName }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim()));

        return full.Length > 0 ? full : username;
    }

    /// <summary>
    /// Looks up every account named by a batch of <c>CreatedBy</c> values, in one query.
    /// </summary>
    /// <remarks>
    /// Values that are not ids are ignored: rows written before accounts were ids carry the name
    /// itself, and <see cref="Label"/> hands those back untouched.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        ApplicationDbContext db,
        IEnumerable<string?> createdByValues,
        CancellationToken cancellationToken)
    {
        var ids = createdByValues
            .Select(value => Guid.TryParse(value?.Trim(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.Username, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            user => user.Id,
            user => Format(user.FirstName, user.LastName, user.Username));
    }

    /// <summary>
    /// What to show for one <c>CreatedBy</c>, or null when there is nothing worth showing.
    /// </summary>
    /// <remarks>
    /// An id that names no account resolves to null rather than to itself. A deleted account's GUID
    /// is not an answer to "who captured this", and a caller that wants to say something in its place
    /// can say it in words — which is what the console does.
    /// </remarks>
    public static string? Label(string? createdBy, IReadOnlyDictionary<Guid, string> names)
    {
        var value = createdBy?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!Guid.TryParse(value, out var id))
        {
            // Rows from before accounts were ids carried the name itself.
            return value;
        }

        return names.TryGetValue(id, out var name) ? name : null;
    }
}
