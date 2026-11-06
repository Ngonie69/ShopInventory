namespace ShopInventory.Features.CreditNotes.Queries.GetCustomerCreditNotes;

/// <summary>A customer's credit notes over a period, newest first.</summary>
public sealed class CustomerCreditNotesResult
{
    public List<CustomerCreditNoteDto> CreditNotes { get; init; } = [];
}
