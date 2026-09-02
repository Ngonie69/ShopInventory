using System.Text.Json;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CreditNotes;

/// <summary>
/// Records a credit note that exists in SAP but could not be fiscalised, so the Exception Center
/// shows it to a person. Shared by the create path and the approval-add path: the document is in SAP
/// either way, and the fiscal receipt is what is still owed.
/// </summary>
internal static class CreditNoteFiscalisationIncidents
{
    public const string Source = "credit-note-fiscalization";

    /// <summary>Never throws — a failure to record the incident must not fail the credit note.</summary>
    public static async Task CaptureAsync(
        ApplicationDbContext context,
        ILogger logger,
        string reference,
        int? sapDocNum,
        string cardCode,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            var incident = new ExceptionCenterIncidentEntity
            {
                Source = Source,
                Category = "Fiscalisation",
                Title = "Credit note fiscalization issue",
                Reference = string.IsNullOrWhiteSpace(reference)
                    ? $"SAP Credit Note {sapDocNum}"
                    : reference,
                Status = "RequiresReview",
                SourceSystem = "CreditNote",
                Provider = "Fiscalisation",
                LastError = message.Length > 2000 ? message[..2000] : message,
                RetryCount = 0,
                MaxRetries = 0,
                CanRetry = false,
                CreatedAtUtc = now,
                OccurredAtUtc = now,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    SapDocNum = sapDocNum,
                    CardCode = cardCode
                })
            };

            context.ExceptionCenterIncidents.Add(incident);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to capture credit note fiscalization incident for {Reference}", reference);
        }
    }
}
