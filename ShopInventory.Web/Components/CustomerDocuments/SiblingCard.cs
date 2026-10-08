namespace ShopInventory.Web.Components.CustomerDocuments;

/// <summary>Another SAP card the same shop trades under, in another currency.</summary>
public sealed record SiblingCard(string CardCode, string CardName, string? Currency);
