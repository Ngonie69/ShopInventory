using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One vending cashier's cash-up for one business day at one depot warehouse: the float they started
/// with, the cash counted at the end, what the slips say for each non-cash tender, and what was banked.
/// </summary>
/// <remarks>
/// <para>
/// A supervisor (a portal Cashier) keys it from the cashier's count and an Admin approves or queries it.
/// Nobody approves a cash-up they keyed or edited. A variance in any currency or tender has to carry a
/// written reason before it can be approved.
/// </para>
/// <para>
/// The expected figures are the cashier's vending sales that day (<see cref="DesktopSaleEntity"/> rows
/// under <c>KefalosVending</c>, by <see cref="DesktopSaleEntity.CreatedBy"/>, warehouse and
/// <see cref="DesktopSaleEntity.DocDate"/>). They are copied onto the lines whenever the count is saved,
/// so an approval is of the figures the counter saw; a sale that lands afterwards makes the approval
/// refuse until the count is saved again against it.
/// </para>
/// <para>
/// Money is per currency and never added across currencies: USD and ZiG are separate lines with separate
/// variances.
/// </para>
/// </remarks>
public class VendingCashUpEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The vending account whose sales are being reconciled (a CartVendor user).</summary>
    public Guid CashierUserId { get; set; }

    /// <summary>The cashier's username as it stood when the cash-up was keyed.</summary>
    [MaxLength(100)]
    public string CashierUsername { get; set; } = string.Empty;

    /// <summary>The depot: the business partner the cashier sells on, as it stood when keyed.</summary>
    [MaxLength(50)]
    public string? BusinessPartnerCode { get; set; }

    /// <summary>The depot warehouse the cashier's sales drew on.</summary>
    [MaxLength(50)]
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>The trading day, a date with no time, matched against the sales' DocDate.</summary>
    public DateTime BusinessDate { get; set; }

    /// <summary>One of <c>VendingCashUpStatuses</c>.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    /// <summary>Why the count does not agree with the sales. Required before a variance is approved.</summary>
    [MaxLength(500)]
    public string? VarianceReason { get; set; }

    /// <summary>Anything else the supervisor wants kept with the count.</summary>
    [MaxLength(1000)]
    public string? Note { get; set; }

    /// <summary>
    /// Bumped on every save. A save or decision carries the revision it was made against and is refused
    /// if the row has moved on, so two people working the same cash-up cannot overwrite each other.
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Whether the first count agreed with the sales in every currency and tender.</summary>
    public bool BalancedOnFirstCount { get; set; }

    public Guid? CapturedByUserId { get; set; }

    [MaxLength(100)]
    public string? CapturedByName { get; set; }

    public DateTime CapturedAt { get; set; }

    /// <summary>Who last saved the count, when that was someone other than the first person.</summary>
    public Guid? UpdatedByUserId { get; set; }

    [MaxLength(100)]
    public string? UpdatedByName { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Everyone who has ever saved this count, comma-separated. An Admin on this list may not approve it:
    /// checking only the first and last saver would let someone who edited it in between sign it off.
    /// </summary>
    [MaxLength(800)]
    public string SavedByUserIds { get; set; } = string.Empty;

    public Guid? ApprovedByUserId { get; set; }

    [MaxLength(100)]
    public string? ApprovedByName { get; set; }

    public DateTime? ApprovedAt { get; set; }

    /// <summary>The Admin's question when the cash-up was sent back. Kept after it is answered.</summary>
    [MaxLength(500)]
    public string? QueryNote { get; set; }

    public Guid? QueriedByUserId { get; set; }

    [MaxLength(100)]
    public string? QueriedByName { get; set; }

    public DateTime? QueriedAt { get; set; }

    public List<VendingCashUpCurrencyEntity> Currencies { get; set; } = new();

    public List<VendingCashUpTenderEntity> Tenders { get; set; } = new();

    public List<VendingCashUpDenominationEntity> Denominations { get; set; } = new();
}

/// <summary>The cash side of a cash-up in one currency.</summary>
public class VendingCashUpCurrencyEntity
{
    [Key]
    public int Id { get; set; }

    public int CashUpId { get; set; }

    public VendingCashUpEntity CashUp { get; set; } = null!;

    /// <summary><c>USD</c> or <c>ZIG</c>.</summary>
    [MaxLength(10)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>The float in the drawer when the day began. It is in the count, so it is not takings.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal OpeningFloat { get; set; }

    /// <summary>The cash sales the count was made against, copied when it was saved.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal ExpectedCash { get; set; }

    /// <summary>How many cash sales made up <see cref="ExpectedCash"/>.</summary>
    public int ExpectedCashSales { get; set; }

    /// <summary>Everything in the drawer at the end of the day, float included.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CountedCash { get; set; }

    /// <summary>What was deposited at the bank out of the count.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Banked { get; set; }

    [MaxLength(100)]
    public string? DepositReference { get; set; }
}

/// <summary>A non-cash tender in one currency: what the slips say beside what the sales say.</summary>
public class VendingCashUpTenderEntity
{
    [Key]
    public int Id { get; set; }

    public int CashUpId { get; set; }

    public VendingCashUpEntity CashUp { get; set; } = null!;

    [MaxLength(10)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>A <c>TenderTypes</c> name other than Cash, or "Not recorded".</summary>
    [MaxLength(30)]
    public string Tender { get; set; } = string.Empty;

    /// <summary>The sales under this tender, copied when the count was saved.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Expected { get; set; }

    /// <summary>What the cashier's slips or statement say was taken.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Declared { get; set; }
}

/// <summary>How many of one note or coin were counted. The lines of a currency add up to its count.</summary>
public class VendingCashUpDenominationEntity
{
    [Key]
    public int Id { get; set; }

    public int CashUpId { get; set; }

    public VendingCashUpEntity CashUp { get; set; } = null!;

    [MaxLength(10)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>The face value: 100, 50, … 0.01.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Value { get; set; }

    public int Quantity { get; set; }
}
