using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Web.Data;

/// <summary>
/// Entity for storing cached product data from SAP in local PostgreSQL database
/// </summary>
public class CachedProduct
{
    [Key]
    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemName { get; set; }

    [MaxLength(50)]
    public string? BarCode { get; set; }

    [MaxLength(20)]
    public string? ItemType { get; set; }

    public bool ManagesBatches { get; set; }

    public decimal Price { get; set; }

    [MaxLength(20)]
    public string? DefaultWarehouse { get; set; }

    [MaxLength(20)]
    public string? UoM { get; set; }

    /// <summary>
    /// When this product was last synced from SAP
    /// </summary>
    public DateTime LastSyncedAt { get; set; }

    /// <summary>
    /// Whether this product is still active in SAP
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Entity for storing cached price data from SAP
/// </summary>
public class CachedPrice
{
    [Key]
    public int Id { get; set; }

    [MaxLength(50)]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemName { get; set; }

    public decimal Price { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public DateTime LastSyncedAt { get; set; }
}

/// <summary>
/// Entity for storing cached business partner data from SAP
/// </summary>
public class CachedBusinessPartner
{
    [Key]
    [MaxLength(50)]
    public string CardCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CardName { get; set; }

    [MaxLength(10)]
    public string? CardType { get; set; }

    [MaxLength(20)]
    public string? GroupCode { get; set; }

    [MaxLength(50)]
    public string? Phone1 { get; set; }

    [MaxLength(50)]
    public string? Phone2 { get; set; }

    [MaxLength(100)]
    public string? Email { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(50)]
    public string? Country { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public decimal? Balance { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime LastSyncedAt { get; set; }
}

/// <summary>
/// Entity for storing cached warehouse data from SAP
/// </summary>
public class CachedWarehouse
{
    [Key]
    [MaxLength(20)]
    public string WarehouseCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? WarehouseName { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(200)]
    public string? Street { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(50)]
    public string? Country { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime LastSyncedAt { get; set; }
}

/// <summary>
/// Entity for storing cached G/L account data from SAP
/// </summary>
public class CachedGLAccount
{
    [Key]
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Name { get; set; }

    [MaxLength(50)]
    public string? AccountType { get; set; }

    [MaxLength(10)]
    public string? Currency { get; set; }

    public decimal Balance { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime LastSyncedAt { get; set; }
}

/// <summary>
/// Entity for storing cached cost centre (profit center) data from SAP
/// These rarely change and are cached locally for performance
/// </summary>
public class CachedCostCentre
{
    [Key]
    [MaxLength(50)]
    public string CenterCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CenterName { get; set; }

    /// <summary>
    /// Dimension type in SAP (1-5)
    /// </summary>
    public int Dimension { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    public DateTime LastSyncedAt { get; set; }
}

/// <summary>
/// Tracks when each cache type was last synced
/// </summary>
public class CacheSyncInfo
{
    [Key]
    [MaxLength(50)]
    public string CacheKey { get; set; } = string.Empty;

    public DateTime LastSyncedAt { get; set; }

    public int ItemCount { get; set; }

    public bool SyncSuccessful { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }
}

/// <summary>
/// Entity for storing cached stock quantities per warehouse
/// </summary>
public class CachedWarehouseStock
{
    [Key]
    public int Id { get; set; }

    [MaxLength(50)]
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ItemName { get; set; }

    [MaxLength(50)]
    public string? BarCode { get; set; }

    [MaxLength(20)]
    [Required]
    public string WarehouseCode { get; set; } = string.Empty;

    public decimal InStock { get; set; }

    public decimal Committed { get; set; }

    public decimal Ordered { get; set; }

    public decimal Available { get; set; }

    [MaxLength(20)]
    public string? UoM { get; set; }

    /// <summary>
    /// When this stock record was last synced from SAP
    /// </summary>
    public DateTime LastSyncedAt { get; set; }
}

/// <summary>
/// Entity for caching incoming payments from SAP
/// </summary>
public class CachedIncomingPayment
{
    [Key]
    public int Id { get; set; }

    public int DocEntry { get; set; }

    public int DocNum { get; set; }

    public DateTime? DocDate { get; set; }

    public DateTime? DocDueDate { get; set; }

    [MaxLength(50)]
    public string? CardCode { get; set; }

    [MaxLength(200)]
    public string? CardName { get; set; }

    [MaxLength(10)]
    public string? DocCurrency { get; set; }

    public decimal CashSum { get; set; }

    public decimal CheckSum { get; set; }

    public decimal TransferSum { get; set; }

    public decimal CreditSum { get; set; }

    public decimal DocTotal { get; set; }

    [MaxLength(500)]
    public string? Remarks { get; set; }

    [MaxLength(100)]
    public string? TransferReference { get; set; }

    public DateTime? TransferDate { get; set; }

    [MaxLength(50)]
    public string? TransferAccount { get; set; }

    /// <summary>
    /// JSON serialized PaymentInvoices
    /// </summary>
    public string? PaymentInvoicesJson { get; set; }

    /// <summary>
    /// JSON serialized PaymentChecks
    /// </summary>
    public string? PaymentChecksJson { get; set; }

    /// <summary>
    /// JSON serialized PaymentCreditCards
    /// </summary>
    public string? PaymentCreditCardsJson { get; set; }

    /// <summary>
    /// When this record was last synced from SAP
    /// </summary>
    public DateTime LastSyncedAt { get; set; }
}

/// <summary>
/// Entity for caching inventory transfers from SAP
/// </summary>
public class CachedInventoryTransfer
{
    [Key]
    public int Id { get; set; }

    public int DocEntry { get; set; }

    public int DocNum { get; set; }

    public DateTime? DocDate { get; set; }

    public DateTime? DueDate { get; set; }

    [MaxLength(20)]
    public string? FromWarehouse { get; set; }

    [MaxLength(20)]
    public string? ToWarehouse { get; set; }

    [MaxLength(500)]
    public string? Comments { get; set; }

    /// <summary>
    /// JSON serialized transfer lines
    /// </summary>
    public string? LinesJson { get; set; }

    /// <summary>
    /// When this record was last synced from SAP
    /// </summary>
    public DateTime LastSyncedAt { get; set; }
}
