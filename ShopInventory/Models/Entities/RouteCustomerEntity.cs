using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models.Entities;

public class RouteCustomerEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string AssignedBusinessPartnerCode { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Code { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = null!;

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? VatNumber { get; set; }

    public bool IsActive { get; set; } = true;

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}