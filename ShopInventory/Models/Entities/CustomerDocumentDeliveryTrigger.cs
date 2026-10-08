namespace ShopInventory.Models.Entities;

/// <summary>Why a customer document delivery exists.</summary>
public enum CustomerDocumentDeliveryTrigger
{
    /// <summary>Queued on its own for a customer who asked to receive their invoices.</summary>
    Auto = 0,

    /// <summary>A member of staff pressed Send.</summary>
    Manual = 1,

    /// <summary>A cashier sent a till receipt to the number a walk-in gave at the counter.</summary>
    Counter = 2
}
