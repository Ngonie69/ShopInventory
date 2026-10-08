namespace ShopInventory.Features.CustomerDocuments;

/// <summary>Who a WhatsApp number is saved on: an account customer's card or a route customer.</summary>
internal sealed record ContactOwner(string? CardCode, int? RouteCustomerId, string Name);
