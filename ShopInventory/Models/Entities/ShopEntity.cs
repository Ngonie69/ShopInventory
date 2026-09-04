using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A retail shop: the business partner its sales are invoiced to, the warehouse its stock leaves, and
/// the cost centre its takings are booked against, held together as one record.
///
/// A till used to assemble this identity from two places that nothing reconciled. The desktop app
/// carried a hardcoded list of shops — Farm, Graniteside, Machipisa — and the operator picked one at
/// setup; that choice decided which warehouse the till <em>read stock and history for</em>. The three
/// codes on the account decided which warehouse it <em>sold from</em>. A till reading one shop's stock
/// while selling from another's was a mis-click away, and opening a fourth shop needed an app release.
///
/// The three codes are one unit because they only make sense together: a shop's business partner draws
/// from that shop's warehouse and books to that shop's cost centre. Set per operator they drift — five
/// operators at a shop, one warehouse mis-picked, and a day's takings land on the wrong partner. Set
/// here they are stated once and every till operator at the shop inherits them.
/// </summary>
/// <remarks>
/// Lives in the API database rather than the Web one because <see cref="User"/> does, and
/// <see cref="User.ShopId"/> is a real foreign key rather than a loose code.
/// </remarks>
[Index(nameof(Code), IsUnique = true)]
[Index(nameof(WarehouseCode))]
public class ShopEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Short stable identifier — what a report groups on, and what survives a rename.
    /// </summary>
    /// <remarks>
    /// Deliberately not the warehouse code, though today every shop has its own. The two answer
    /// different questions: this names the shop as a place people work, the warehouse names where SAP
    /// holds its stock. Tying them would make a shop that ever changed warehouse change identity, and
    /// take its sales history with it.
    /// </remarks>
    [Required]
    [MaxLength(30)]
    public string Code { get; set; } = null!;

    /// <summary>The shop as people say it: "Machipisa".</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The SAP business partner this shop's sales are invoiced to, and whose price list it sells at.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string BusinessPartnerCode { get; set; } = null!;

    /// <summary>
    /// The warehouse this shop's stock leaves. Exactly one — a business partner draws from one
    /// warehouse, and <see cref="Common.Sales.SellingAccountResolver"/> refuses to sell on an
    /// ambiguous answer.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string WarehouseCode { get; set; } = null!;

    /// <summary>
    /// The cost centre this shop's takings are booked against.
    /// </summary>
    /// <remarks>
    /// Optional, unlike the two above. The cost centre is a reporting dimension SAP will default when
    /// it is absent, so requiring it would stop an otherwise correctly configured shop from trading in
    /// exchange for nothing that affects the money — the same reasoning
    /// <see cref="Common.Sales.SellingAccountResolver"/> already applies per account.
    /// </remarks>
    [MaxLength(50)]
    public string? CostCentreCode { get; set; }

    /// <summary>
    /// Whether the shop is trading. A closed shop is deactivated rather than deleted, so its sales
    /// history keeps a shop to belong to.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid? UpdatedByUserId { get; set; }

    public User? UpdatedByUser { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>The accounts that work this shop's till.</summary>
    public ICollection<User> Users { get; set; } = new List<User>();
}
