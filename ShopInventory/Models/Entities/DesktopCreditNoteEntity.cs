using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>A fiscal credit against a desktop receipt, independent of SAP document creation.</summary>
[Index(nameof(RequestKey), IsUnique = true)]
[Index(nameof(SaleId), nameof(Status))]
public sealed class DesktopCreditNoteEntity
{
    public Guid Id { get; set; }
    public int SaleId { get; set; }
    public DesktopSaleEntity Sale { get; set; } = null!;
    [MaxLength(32)] public string RequestKey { get; set; } = "";
    [MaxLength(64)] public string RequestHash { get; set; } = "";
    [MaxLength(50)] public string Number { get; set; } = "";
    [MaxLength(100)] public string OriginalFiscalNumber { get; set; } = "";
    [MaxLength(500)] public string Reason { get; set; } = "";
    [MaxLength(10)] public string Currency { get; set; } = "";
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [MaxLength(30)] public string Status { get; set; } = "Prepared";
    public string PlanJson { get; set; } = "";
    public string? FiscalResultJson { get; set; }
    public string? Message { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmitStartedAtUtc { get; set; }
    public DateTime? FiscalisedAtUtc { get; set; }
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
}
