namespace ShopInventory.Web.Common;

/// <summary>
/// The first name a dashboard greets by, taken from the username. An address local part carries
/// the name in one of a few shapes — first.last, first_last, or just the name — so all three reduce
/// to the same greeting. The same rule StockDashboard and WashBayDashboard each carry inline.
/// </summary>
public static class DashboardGreeting
{
    public static string FirstName(string? username)
    {
        var localName = (username ?? string.Empty).Split('@')[0];
        var firstName = localName
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstName)) return "there";
        return firstName.Length == 1
            ? firstName.ToUpperInvariant()
            : char.ToUpperInvariant(firstName[0]) + firstName[1..];
    }
}
