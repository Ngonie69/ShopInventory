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

    /// <summary>
    /// The family name, for a route customer who is a person rather than a shop.
    /// </summary>
    /// <remarks>
    /// This table holds two populations. A van's route customer is a <i>shop</i>, and its whole name
    /// is in <see cref="Name"/> — which is what every handset displays, through
    /// <c>GetVanSalesChannelCustomers</c>. A cart vendor is a <i>person</i>, captured at a counter as
    /// a first name and a surname.
    ///
    /// So this sits beside <see cref="Name"/> rather than <see cref="Name"/> being narrowed to mean
    /// "first name". Narrowing it would have changed what is on the screen of every van in the
    /// field, for rows nobody had touched. It is null for every shop, and stays null.
    /// </remarks>
    [MaxLength(100)]
    public string? Surname { get; set; }

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