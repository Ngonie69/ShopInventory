namespace ShopInventory.Web.Models;

// Hand mirrors of the API's GetStockAsAt / GetStockMovements results
// (ShopInventory/Features/Stock/Queries). Nullability follows the API's records exactly: a field
// declared non-null here that the API can leave out deserialises to a default and reads as data.

/// <summary>What a warehouse held at one moment, rebuilt from the stock ledger.</summary>
public sealed class StockAsAtResponse
{
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime AsAtUtc { get; set; }

    /// <summary>False when no finished morning snapshot covers the moment.</summary>
    public bool Recorded { get; set; }

    public DateTime? LedgerDay { get; set; }
    public DateTime? SnapshotTakenAtUtc { get; set; }

    /// <summary>The first ledger day this warehouse can be read for.</summary>
    public DateTime? RecordedFrom { get; set; }

    public List<StockAsAtItem> Items { get; set; } = [];
}

public sealed class StockAsAtItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public decimal OnHand { get; set; }
    public decimal OpeningQuantity { get; set; }
    public DateTime? LastMovementAtUtc { get; set; }
}

public sealed class StockMovementsResponse
{
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public bool Truncated { get; set; }
    public List<StockLedgerMovement> Movements { get; set; } = [];
    public List<StockDayOpening> Openings { get; set; } = [];
}

public sealed class StockLedgerMovement
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime LedgerDay { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string? DocumentKey { get; set; }
}

public sealed class StockDayOpening
{
    public DateTime LedgerDay { get; set; }
    public DateTime TakenAtUtc { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class StockMovementTotalsResponse
{
    public string WarehouseCode { get; set; } = string.Empty;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public List<StockMovementTotal> Items { get; set; } = [];
}

public sealed class StockMovementTotal
{
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Units sales took, net of returns.</summary>
    public decimal Sold { get; set; }

    public decimal TransferredIn { get; set; }

    /// <summary>Positive.</summary>
    public decimal TransferredOut { get; set; }

    /// <summary>The hourly SAP corrections' net, signed.</summary>
    public decimal Corrected { get; set; }

    public int Movements { get; set; }
}

/// <summary>The ledger's movement kinds, as the API writes them (StockMovementKinds).</summary>
public static class StockLedgerKinds
{
    public const string Commit = "Commit";
    public const string Settle = "Settle";
    public const string Release = "Release";
    public const string Transfer = "Transfer";
    public const string Reconciliation = "Reconciliation";

    /// <summary>How a movement reads to someone running a shop.</summary>
    public static string Describe(string kind, decimal quantity) => kind switch
    {
        Commit or Settle => quantity < 0 ? "Sold" : "Back on the shelf",
        Release => "Back on the shelf",
        Transfer => quantity < 0 ? "Transferred out" : "Transferred in",
        Reconciliation => "SAP correction",
        _ => kind
    };
}
