namespace ShopInventory.Models.Entities;

/// <summary>How a number stopped receiving documents.</summary>
public enum WhatsAppOptOutSource
{
    /// <summary>A member of staff opted it out in the web app, usually because the customer asked.</summary>
    Web = 0,

    /// <summary>The customer replied STOP to the documents number.</summary>
    StopKeyword = 1
}
