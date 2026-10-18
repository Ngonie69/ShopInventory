namespace ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;

public sealed class RetryFailedInvoiceFiscalisationsResult
{
    public int Fiscalised { get; set; }

    /// <summary>The device already held a receipt for the invoice, so nothing new was filed.</summary>
    public int AlreadyHeld { get; set; }

    /// <summary>Refused before anything reached the device: the invoice is not this pass's to file.</summary>
    public int Refused { get; set; }

    public int Failed { get; set; }

    /// <summary>Why the pass left the rest of its invoices for the next one, or null when it went through them all.</summary>
    public string? StoppedBecause { get; set; }

    public int Total => Fiscalised + AlreadyHeld + Refused + Failed;
}
