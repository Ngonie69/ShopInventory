namespace ShopInventory.Services;

public sealed class SapCircuitOpenException(string message) : InvalidOperationException(message);