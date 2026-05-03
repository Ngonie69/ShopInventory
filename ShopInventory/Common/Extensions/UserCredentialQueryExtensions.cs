using ShopInventory.Models;

namespace ShopInventory.Common.Extensions;

public static class UserCredentialQueryExtensions
{
    public static IQueryable<User> WhereUsernameMatches(this IQueryable<User> query, string username)
    {
        var normalizedUsername = NormalizeCredential(username);
        return query.Where(user => user.Username.ToUpper() == normalizedUsername);
    }

    public static IQueryable<User> WhereUsernameOrEmailMatches(this IQueryable<User> query, string credential)
    {
        var normalizedCredential = NormalizeCredential(credential);
        return query.Where(user =>
            user.Username.ToUpper() == normalizedCredential ||
            (user.Email != null && user.Email.ToUpper() == normalizedCredential));
    }

    public static IQueryable<User> WhereEmailMatches(this IQueryable<User> query, string? email)
    {
        var normalizedEmail = NormalizeCredential(email);
        return query.Where(user => user.Email != null && user.Email.ToUpper() == normalizedEmail);
    }

    public static string NormalizeCredential(string? value)
        => value?.Trim().ToUpperInvariant() ?? string.Empty;
}