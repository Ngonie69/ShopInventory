using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>How much of one line was actually handed over.</summary>
public class RecordVanSalesOrderDeliveryLineRequest
{
    [Required]
    public int LineNumber { get; set; }

    /// <summary>
    /// The quantity delivered. Zero is meaningful — it records a line that could not be filled at
    /// all — so it is accepted rather than treated as a missing value.
    /// </summary>
    public decimal QuantityFulfilled { get; set; }
}

/// <summary>
/// What the van actually delivered against an order.
/// </summary>
/// <remarks>
/// Lines left out of this request are left untouched, not zeroed. A rep recording the two lines
/// they were short on must not thereby declare that nothing else arrived.
/// </remarks>
public class RecordVanSalesOrderDeliveryRequest
{
    [Required]
    public List<RecordVanSalesOrderDeliveryLineRequest> Lines { get; set; } = [];
}
