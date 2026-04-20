using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Tracks which products are active/inactive for merchandisers (mobile app product list)
/// </summary>
[Table("MerchandiserProducts")]
public class MerchandiserProductEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The merchandiser user ID
    /// </summary>
    public Guid MerchandiserUserId { get; set; }

    /// <summary>
    /// The product item code from SAP
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Product name (denormalized for convenience)
    /// </summary>
    [MaxLength(200)]
    public string? ItemName { get; set; }

    /// <summary>
    /// Barcode (denormalized from SAP OITM.CodeBars)
    /// </summary>
    [MaxLength(100)]
    public string? BarCode { get; set; }

    /// <summary>
    /// Unit of measure (denormalized from SAP OITM.SalUnitMsr)
    /// </summary>
    [MaxLength(50)]
    public string? UoM { get; set; }

    /// <summary>
    /// Product category/group (denormalized from SAP OITM.U_ItemGroup)
    /// </summary>
    [MaxLength(100)]
    public string? Category { get; set; }

    /// <summary>
    /// Whether this product is active for the merchandiser
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this assignment was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this assignment was last updated
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Who last modified this assignment
    /// </summary>
    [MaxLength(50)]
    public string? UpdatedBy { get; set; }

    // Navigation properties
    [ForeignKey(nameof(MerchandiserUserId))]
    public User? MerchandiserUser { get; set; }
}
