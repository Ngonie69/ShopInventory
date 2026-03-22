using System.Text.Json.Serialization;

namespace ShopInventory.Web.Models;

public enum QuotationStatus
{
    Draft = 0,
    Pending = 1,
    Approved = 2,
    Sent = 3,
    Accepted = 4,
    Rejected = 5,
    Expired = 6,
    Converted = 7,
    Cancelled = 8
}

public class QuotationDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("sapDocEntry")]
    public int? SAPDocEntry { get; set; }

    [JsonPropertyName("sapDocNum")]
    public int? SAPDocNum { get; set; }

    [JsonPropertyName("quotationNumber")]
    public string QuotationNumber { get; set; } = null!;

    [JsonPropertyName("quotationDate")]
    public DateTime QuotationDate { get; set; }

    [JsonPropertyName("validUntil")]
    public DateTime? ValidUntil { get; set; }

    [JsonPropertyName("cardCode")]
    public string CardCode { get; set; } = null!;

    [JsonPropertyName("cardName")]
    public string? CardName { get; set; }

    [JsonPropertyName("customerRefNo")]
    public string? CustomerRefNo { get; set; }

    [JsonPropertyName("contactPerson")]
    public string? ContactPerson { get; set; }

    [JsonPropertyName("status")]
    public QuotationStatus Status { get; set; }

    public string StatusName => Status.ToString();

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("termsAndConditions")]
    public string? TermsAndConditions { get; set; }

    [JsonPropertyName("salesPersonCode")]
    public int? SalesPersonCode { get; set; }

    [JsonPropertyName("salesPersonName")]
    public string? SalesPersonName { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("exchangeRate")]
    public decimal ExchangeRate { get; set; }

    [JsonPropertyName("subTotal")]
    public decimal SubTotal { get; set; }

    [JsonPropertyName("taxAmount")]
    public decimal TaxAmount { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("discountAmount")]
    public decimal DiscountAmount { get; set; }

    [JsonPropertyName("docTotal")]
    public decimal DocTotal { get; set; }

    [JsonPropertyName("shipToAddress")]
    public string? ShipToAddress { get; set; }

    [JsonPropertyName("billToAddress")]
    public string? BillToAddress { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("createdByUserId")]
    public Guid? CreatedByUserId { get; set; }

    [JsonPropertyName("createdByUserName")]
    public string? CreatedByUserName { get; set; }

    [JsonPropertyName("approvedByUserId")]
    public Guid? ApprovedByUserId { get; set; }

    [JsonPropertyName("approvedByUserName")]
    public string? ApprovedByUserName { get; set; }

    [JsonPropertyName("approvedDate")]
    public DateTime? ApprovedDate { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("salesOrderId")]
    public int? SalesOrderId { get; set; }

    [JsonPropertyName("isSynced")]
    public bool IsSynced { get; set; }

    [JsonPropertyName("isExpired")]
    public bool IsExpired { get; set; }

    [JsonPropertyName("lines")]
    public List<QuotationLineDto> Lines { get; set; } = new();
}

public class QuotationLineDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("lineNum")]
    public int LineNum { get; set; }

    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = null!;

    [JsonPropertyName("itemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal DiscountPercent { get; set; }

    [JsonPropertyName("taxPercent")]
    public decimal TaxPercent { get; set; }

    [JsonPropertyName("lineTotal")]
    public decimal LineTotal { get; set; }

    [JsonPropertyName("warehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("uoMCode")]
    public string? UoMCode { get; set; }
}

public class CreateQuotationRequest
{
    public DateTime? ValidUntil { get; set; }
    public string CardCode { get; set; } = null!;
    public string? CardName { get; set; }
    public string? CustomerRefNo { get; set; }
    public string? ContactPerson { get; set; }
    public string? Comments { get; set; }
    public string? TermsAndConditions { get; set; }
    public int? SalesPersonCode { get; set; }
    public string? SalesPersonName { get; set; }
    public string? Currency { get; set; } = "USD";
    public decimal DiscountPercent { get; set; }
    public string? ShipToAddress { get; set; }
    public string? BillToAddress { get; set; }
    public string? WarehouseCode { get; set; }
    public List<CreateQuotationLineRequest> Lines { get; set; } = new();
}

public class CreateQuotationLineRequest
{
    public string ItemCode { get; set; } = null!;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent { get; set; }
    public string? WarehouseCode { get; set; }
    public string? UoMCode { get; set; }
}

public class UpdateQuotationStatusRequest
{
    public QuotationStatus Status { get; set; }
    public string? Comments { get; set; }
}

public class QuotationListResponse
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("quotations")]
    public List<QuotationDto> Quotations { get; set; } = new();
}
