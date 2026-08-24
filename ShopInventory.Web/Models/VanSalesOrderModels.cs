using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

/// <summary>
/// The web-side view of the van sales customer ordering contract.
/// </summary>
/// <remarks>
/// Declared here rather than shared with the API project, matching how every other web model in
/// this application is done. Enum values arrive as names because the API annotates them with
/// <c>JsonStringEnumConverter</c>, so these must be spelled the same way.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VanSalesOrderStatusModel
{
    Accepted = 0,
    Cancelled = 1,
    Fulfilled = 2,
    PartiallyFulfilled = 3,
    Expired = 4
}

public class VanSalesOrderLineModel
{
    public int LineNumber { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityFulfilled { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>
    /// What the operator types when recording the delivery, seeded with the ordered quantity.
    /// </summary>
    /// <remarks>
    /// Seeded full rather than empty because delivering everything is the common case, and a form
    /// that starts at zero invites a rep in a hurry to submit it — recording a whole round as
    /// undelivered.
    /// </remarks>
    [JsonIgnore]
    public decimal DeliveredInput { get; set; }
}

public class VanSalesOrderModel
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string ClientRequestId { get; set; } = string.Empty;
    public string CustomerCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string? RouteCode { get; set; }
    public DateTime? RequestedVisitDate { get; set; }
    public VanSalesOrderStatusModel Status { get; set; }
    public string? Currency { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DocTotal { get; set; }
    public string? CustomerNotes { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public List<VanSalesOrderLineModel> Lines { get; set; } = [];
}

/// <summary>One item totalled across every order on the run — what the depot loads to.</summary>
public class VanSalesLoadLineModel
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal QuantityOrdered { get; set; }
    public int OrderCount { get; set; }
}

public class VanSalesRouteLoadModel
{
    public DateTime? VisitDate { get; set; }
    public string? RouteCode { get; set; }
    public int OrderCount { get; set; }
    public decimal DocTotal { get; set; }
    public List<VanSalesLoadLineModel> LoadLines { get; set; } = [];
    public List<VanSalesOrderModel> Orders { get; set; } = [];
}

public class RecordVanSalesDeliveryLineModel
{
    public int LineNumber { get; set; }
    public decimal QuantityFulfilled { get; set; }
}

public class RecordVanSalesDeliveryModel
{
    public List<RecordVanSalesDeliveryLineModel> Lines { get; set; } = [];
}

public class VanSalesOrderConversionModel
{
    public int VanSalesOrderId { get; set; }
    public string VanSalesOrderNumber { get; set; } = string.Empty;
    public int SalesOrderId { get; set; }
    public string SalesOrderNumber { get; set; } = string.Empty;
}
