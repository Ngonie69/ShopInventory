namespace ShopInventory.Common.Pods;

internal static class PodInvoiceCreatorLocations
{
    private static readonly IReadOnlyDictionary<int, (string UserName, string Location)> CreatorLocations =
        new Dictionary<int, (string UserName, string Location)>
        {
            [16] = ("Wallace", "Cheeseman DC Harare"),
            [34] = ("Rose", "Factory-Dispatch"),
            [15] = ("Fiona Cheeseman", "Cheeseman DC Meyrick"),
            [74] = ("Sarah", "Factory-Dispatch"),
            [77] = ("User 77", "Graniteside"),
            [29] = ("Cheeseman Bulawayo Manager", "Cheeseman DC Byo"),
            [50] = ("Darlington", "Cheeseman DC Harare"),
            [12] = ("Alice", "Factory-Dispatch"),
            [27] = ("Jenny", "Factory-Dispatch")
        };

    public static (string UserName, string Location)? GetCreatorLocation(int? userId)
    {
        if (!userId.HasValue)
            return null;

        return CreatorLocations.TryGetValue(userId.Value, out var creatorLocation)
            ? creatorLocation
            : null;
    }
}