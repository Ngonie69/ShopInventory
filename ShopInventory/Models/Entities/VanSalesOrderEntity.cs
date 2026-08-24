using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Where a van sales customer's own order stands.
/// </summary>
/// <remarks>
/// Shorter than <see cref="SalesOrderStatus"/> and deliberately so. This document is a request from
/// a shop, not a document in the ERP: there is no Draft (a draft never leaves the handset), no
/// Approved (orders are auto-accepted — the rep adjusts at delivery instead), and no OnHold. What
/// happens to it commercially happens to the sales order it is converted into, which has its own
/// statuses for that.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VanSalesOrderStatus
{
    /// <summary>
    /// Received and on the van's list. The only state a customer's order arrives in.
    /// </summary>
    Accepted = 0,

    /// <summary>Withdrawn by the customer before the cut-off, or by staff.</summary>
    Cancelled = 1,

    /// <summary>Delivered as ordered.</summary>
    Fulfilled = 2,

    /// <summary>
    /// Delivered short. The normal outcome of a van that loaded less than the round asked for, not
    /// an error state.
    /// </summary>
    PartiallyFulfilled = 3,

    /// <summary>
    /// The call came and went without a delivery being recorded. Closes the order so it stops
    /// appearing on load lists, without claiming it was filled.
    /// </summary>
    Expired = 4
}

/// <summary>
/// An order a van sales customer placed for themselves, on their own phone.
///
/// This is the WhatsApp message made into data. It deliberately does <em>not</em> reuse
/// <see cref="SalesOrderEntity"/>, which it otherwise resembles closely enough to be tempting.
/// That table feeds the SAP posting jobs, the staff order lists and the sales reports; letting an
/// unvetted, customer-facing channel write straight into it would mean auditing every existing
/// query in the system for "is this row one a shopkeeper typed?". This table is the intake, the
/// existing pipeline stays the outlet, and <see cref="ConvertedSalesOrderId"/> is the one door
/// between them — opened explicitly, by staff, and never by the customer.
/// </summary>
/// <remarks>
/// Snapshots rather than joins for the customer and route names, for the reason
/// <see cref="VanRouteDayEntity"/> gives for its own: route customers are hard-deleted and freely
/// reassigned, and an order from March must still say which shop placed it and which van was to
/// carry it, whatever has happened to either since.
/// </remarks>
[Index(nameof(ClientRequestId), IsUnique = true)]
[Index(nameof(OrderNumber), IsUnique = true)]
[Index(nameof(VanSalesCustomerAccountId), nameof(ReceivedAtUtc))]
[Index(nameof(RouteCustomerId))]
[Index(nameof(RequestedVisitDate), nameof(Status))]
public class VanSalesOrderEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The reference both sides quote: <c>VSO-20260824-0001</c>.
    /// </summary>
    /// <remarks>
    /// Its own series, not the sales order one. A customer ringing up about "order 41" must not be
    /// answered with somebody else's document, and the two numbers live entirely different lives.
    /// </remarks>
    [Required]
    [MaxLength(50)]
    public string OrderNumber { get; set; } = null!;

    public int VanSalesCustomerAccountId { get; set; }

    [ForeignKey(nameof(VanSalesCustomerAccountId))]
    public VanSalesCustomerAccountEntity? Account { get; set; }

    public int RouteCustomerId { get; set; }

    [ForeignKey(nameof(RouteCustomerId))]
    public RouteCustomerEntity? RouteCustomer { get; set; }

    // --- The shop and its round, as they stood when the order was placed ---

    [Required]
    [MaxLength(50)]
    public string RouteCustomerCode { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string RouteCustomerName { get; set; } = null!;

    /// <summary>The business partner the shop is served by — which is to say, its van.</summary>
    [MaxLength(100)]
    public string? AssignedBusinessPartnerCode { get; set; }

    [MaxLength(30)]
    public string? RouteCode { get; set; }

    [MaxLength(100)]
    public string? RouteName { get; set; }

    // --- What was asked for ---

    /// <summary>
    /// The call this order is for, as a bare CAT date. Null when the shop has no calling days
    /// configured, in which case it goes on the next available run.
    /// </summary>
    [Column(TypeName = "date")]
    public DateTime? RequestedVisitDate { get; set; }

    public VanSalesOrderStatus Status { get; set; } = VanSalesOrderStatus.Accepted;

    [MaxLength(10)]
    public string? Currency { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SubTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TaxAmount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DocTotal { get; set; }

    [MaxLength(1000)]
    public string? CustomerNotes { get; set; }

    // --- Provenance ---

    /// <summary>
    /// The handset's idempotency key, minted when the draft was created rather than when it was
    /// sent.
    /// </summary>
    /// <remarks>
    /// The unique index on this column is what makes the whole offline design safe. A van sales
    /// customer orders from places with no signal; the app queues and retries, and a reply lost on
    /// a dead line is indistinguishable from a request that never arrived. Retrying the same key
    /// collides here instead of producing a second delivery.
    /// </remarks>
    [Required]
    [MaxLength(100)]
    public string ClientRequestId { get; set; } = null!;

    /// <summary>When the customer pressed send, by the handset's clock.</summary>
    /// <remarks>
    /// Kept beside <see cref="ReceivedAtUtc"/> rather than instead of it. For a queued order the two
    /// can be days apart, and the gap is the only evidence of how long the app was offline. Never
    /// used to decide anything — a device clock is not a fact — but worth having when a customer
    /// says they ordered on Monday.
    /// </remarks>
    public DateTime? SubmittedAtUtc { get; set; }

    /// <summary>When the server took it. The only timestamp anything is decided on.</summary>
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(200)]
    public string? DeviceInfo { get; set; }

    [MaxLength(50)]
    public string? AppVersion { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    // --- What became of it ---

    /// <summary>
    /// The sales order this was converted into, once staff convert it. The single link to the
    /// existing pipeline, and null for the whole of an order's ordinary life.
    /// </summary>
    public int? ConvertedSalesOrderId { get; set; }

    [ForeignKey(nameof(ConvertedSalesOrderId))]
    public SalesOrderEntity? ConvertedSalesOrder { get; set; }

    public DateTime? ConvertedAtUtc { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    [MaxLength(500)]
    public string? CancellationReason { get; set; }

    public DateTime? DeliveredAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// PostgreSQL's <c>xmin</c> system column, as an optimistic concurrency token.
    /// </summary>
    /// <remarks>
    /// Not <c>[Timestamp] byte[]</c>, which <see cref="SalesOrderEntity"/> uses: Npgsql maps that
    /// to an ordinary <c>bytea</c> that the database never populates or advances, so it reads like
    /// a concurrency check while being inert. <c>xmin</c> is the real thing and is what
    /// <see cref="DailyStockSnapshotItemEntity"/> uses. It matters here because a rep recording a
    /// delivery and a customer cancelling can land on the same row at the same moment.
    /// </remarks>
    [Timestamp]
    public uint Version { get; set; }

    public ICollection<VanSalesOrderLineEntity> Lines { get; set; } = new List<VanSalesOrderLineEntity>();

    /// <summary>Whether the order is still awaiting delivery, and so still on a load list.</summary>
    [NotMapped]
    public bool IsOpen => Status == VanSalesOrderStatus.Accepted;
}

/// <summary>One item on a customer's order.</summary>
/// <remarks>
/// Prices and descriptions are stored, not looked up. The catalogue moves — items are renamed,
/// repriced and withdrawn — and an order has to keep saying what was agreed on the day it was
/// placed, both to settle an argument at the door and because the delivery is invoiced from it.
/// </remarks>
[Index(nameof(VanSalesOrderId))]
public class VanSalesOrderLineEntity
{
    [Key]
    public int Id { get; set; }

    public int VanSalesOrderId { get; set; }

    [ForeignKey(nameof(VanSalesOrderId))]
    public VanSalesOrderEntity? Order { get; set; }

    /// <summary>Position on the order as the customer built it, so it reads back in their order.</summary>
    public int LineNumber { get; set; }

    [Required]
    [MaxLength(50)]
    public string ItemCode { get; set; } = null!;

    [MaxLength(200)]
    public string? ItemDescription { get; set; }

    [MaxLength(50)]
    public string? UoMCode { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal QuantityOrdered { get; set; }

    /// <summary>
    /// What the rep actually handed over. Zero until a delivery is recorded, and never assumed
    /// equal to <see cref="QuantityOrdered"/> — the gap between them is the point of recording it.
    /// </summary>
    [Column(TypeName = "decimal(18,3)")]
    public decimal QuantityFulfilled { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(9,4)")]
    public decimal TaxPercent { get; set; }

    /// <summary>Net of tax, at the quantity ordered.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }
}
