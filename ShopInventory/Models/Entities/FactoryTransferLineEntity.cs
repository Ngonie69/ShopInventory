using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One product on a <see cref="FactoryTransferEntity"/>.
/// </summary>
[Index(nameof(TransferId))]
public sealed class FactoryTransferLineEntity
{
    [Key]
    public int Id { get; set; }

    public int TransferId { get; set; }

    [ForeignKey(nameof(TransferId))]
    public FactoryTransferEntity Transfer { get; set; } = null!;

    public int LineNum { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    [MaxLength(20)]
    public string? UoMCode { get; set; }

    /// <summary>
    /// Where this line was sent from when it differs from the transfer's: a depot delivery loads one
    /// truck from several dispatch warehouses. Anything not accepted goes back here. Null means the
    /// transfer's own source warehouse.
    /// </summary>
    [MaxLength(50)]
    public string? FromWarehouseCode { get; set; }

    /// <summary>What was sent, in the item's inventory unit. Never changed after the send.</summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// What the receiving side took in. Null until decided; zero on a rejected or recalled transfer.
    /// The rest of <see cref="Quantity"/> goes back to the sender's warehouse.
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal? AcceptedQuantity { get; set; }

    /// <summary>
    /// The batches SAP chose for the out leg, as JSON <c>[{"BatchNumber":"…","Quantity":n}]</c>.
    /// The in leg moves exactly these out of the holding warehouse, because FSRW is shared and an
    /// allocation of its own could take another transfer's batch of the same product.
    /// </summary>
    public string? BatchesJson { get; set; }
}
