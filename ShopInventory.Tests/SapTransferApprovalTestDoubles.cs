using System.Net;
using System.Reflection;
using ShopInventory.Common.Stock;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The SAP side of the held-transfer flow, as a Service Layer that remembers what it was asked and
/// changes state when a draft is added: the draft closes, as SAP closes it.
/// </summary>
internal sealed class FakeTransferSap
{
    private bool _saved;

    public FakeTransferSap(SAPStockTransferDraft draft, params SAPApprovalRequest[] requests)
    {
        Draft = draft;
        Requests = requests.ToList();
    }

    public SAPStockTransferDraft Draft { get; set; }
    public List<SAPApprovalRequest> Requests { get; set; }

    /// <summary>The requests as SAP answers once the add has landed. Null keeps <see cref="Requests"/>.</summary>
    public List<SAPApprovalRequest>? RequestsAfterSave { get; init; }

    /// <summary>The draft as SAP reports it once the add has landed — closed, unless a test says otherwise.</summary>
    public SAPStockTransferDraft? DraftAfterSave { get; init; }

    /// <summary>Item flags. Null means the read throws, as a Service Layer that is not answering does.</summary>
    public Dictionary<string, SapItemManagement>? Management { get; init; } = [];

    public int? NewestBefore { get; init; } = 1000;
    public List<InventoryTransfer> TransfersAfter { get; init; } = [];
    public Dictionary<int, InventoryTransfer> Transfers { get; } = [];

    public int? SaveReturns { get; init; }
    public string? RefuseWith { get; init; }

    /// <summary>Thrown by the next save only, so a retry can succeed.</summary>
    public Exception? ThrowOnSave { get; set; }

    /// <summary>Whether the save that throws <see cref="ThrowOnSave"/> landed in SAP anyway.</summary>
    public bool ThrownSaveLanded { get; init; }

    public Dictionary<int, InventoryTransferRequest> TransferRequests { get; } = [];

    /// <summary>The requests SAP closes itself when the draft is added, through the base-line link.</summary>
    public HashSet<int> ClosedBySapOnSave { get; init; } = [];

    public HashSet<int> CloseFails { get; init; } = [];

    /// <summary>Transfers this app made, keyed by the <c>U_OrderNumber</c> marker they carry.</summary>
    public Dictionary<string, InventoryTransfer> AppConversions { get; } = [];

    public List<(int DraftEntry, CancellationToken Token)> Saves { get; } = [];
    public List<int> ClosedRequests { get; } = [];

    /// <summary>What the list read was asked for: the warehouse scope and the page's filters.</summary>
    public List<(IReadOnlyCollection<string>? Scope, SapTransferDraftFilter? Filter, int? Before)> ListReads { get; } = [];

    /// <summary>The drafts the list read answers with, and the count it reports.</summary>
    public List<SAPStockTransferDraft> ListDrafts { get; init; } = [];
    public int ListTotal { get; init; }

    /// <summary>What the kind switch's extra count answers. Null means the count throws.</summary>
    public int? KindCount { get; init; } = 0;

    /// <summary>What each kind count was asked for.</summary>
    public List<SapTransferDraftFilter?> CountReads { get; } = [];

    /// <summary>How many times the item flags were read — never, for a transfer request draft.</summary>
    public int ManagementReads { get; private set; }

    public int? NewestRequestBefore { get; init; } = 20000;
    public List<InventoryTransferRequest> RequestsAfter { get; init; } = [];

    public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
    {
        nameof(ISAPServiceLayerClient.GetStockTransferDraftAsync)
            => Task.FromResult<SAPStockTransferDraft?>((int)args![0]! == Draft.DocEntry ? (_saved ? DraftAfterSave ?? Closed(Draft) : Draft) : null),
        nameof(ISAPServiceLayerClient.GetStockTransferApprovalRequestsAsync)
            => Task.FromResult(_saved ? RequestsAfterSave ?? Requests : Requests),
        nameof(ISAPServiceLayerClient.GetItemManagementAsync) => ReadManagement(),
        nameof(ISAPServiceLayerClient.GetNewestStockTransferDocEntryAsync)
            => Task.FromResult(NewestBefore),
        nameof(ISAPServiceLayerClient.GetStockTransfersAfterAsync)
            => Task.FromResult(TransfersAfter),
        nameof(ISAPServiceLayerClient.GetInventoryTransferByDocEntryAsync)
            => Task.FromResult(Transfers.GetValueOrDefault((int)args![0]!)),
        nameof(ISAPServiceLayerClient.GetInventoryTransferRequestByDocEntryAsync)
            => Task.FromResult(TransferRequests.GetValueOrDefault((int)args![0]!)),
        nameof(ISAPServiceLayerClient.FindStockTransferByOrderNumberAsync)
            => Task.FromResult(AppConversions.GetValueOrDefault((string)args![0]!)),
        nameof(ISAPServiceLayerClient.CloseInventoryTransferRequestAsync) => Close((int)args![0]!),
        nameof(ISAPServiceLayerClient.SaveStockTransferDraftToDocumentAsync) => Save(args!),
        nameof(ISAPServiceLayerClient.GetApprovedStockTransferDraftsAsync) => List(args!),
        nameof(ISAPServiceLayerClient.CountApprovedStockTransferDraftsAsync) => Count(args!),
        nameof(ISAPServiceLayerClient.GetNewestInventoryTransferRequestDocEntryAsync)
            => Task.FromResult(NewestRequestBefore),
        nameof(ISAPServiceLayerClient.GetInventoryTransferRequestsAfterAsync)
            => Task.FromResult(RequestsAfter),
        _ => throw new InvalidOperationException($"{method.Name} was not expected.")
    });

    private Task<IReadOnlyDictionary<string, SapItemManagement>> ReadManagement()
    {
        ManagementReads++;
        return Management is null
            ? Task.FromException<IReadOnlyDictionary<string, SapItemManagement>>(new HttpRequestException("SAP is not answering"))
            : Task.FromResult<IReadOnlyDictionary<string, SapItemManagement>>(Management);
    }

    private Task<int> Count(object?[] args)
    {
        CountReads.Add((SapTransferDraftFilter?)args[1]);
        return KindCount is int count
            ? Task.FromResult(count)
            : Task.FromException<int>(new HttpRequestException("SAP is not answering"));
    }

    private Task<(List<SAPStockTransferDraft> Items, int TotalCount)> List(object?[] args)
    {
        ListReads.Add(((IReadOnlyCollection<string>?)args[3], (SapTransferDraftFilter?)args[4], (int?)args[2]));
        return Task.FromResult((ListDrafts, ListTotal));
    }

    private Task Close(int docEntry)
    {
        if (CloseFails.Contains(docEntry))
        {
            return Task.FromException(new HttpRequestException("SAP is not answering"));
        }

        ClosedRequests.Add(docEntry);
        TransferRequests[docEntry].DocumentStatus = SapDocumentStatuses.Closed;
        return Task.CompletedTask;
    }

    private Task<int?> Save(object?[] args)
    {
        if (ThrowOnSave is { } thrown)
        {
            ThrowOnSave = null;
            if (ThrownSaveLanded)
            {
                Land();
            }

            return Task.FromException<int?>(thrown);
        }

        if (RefuseWith is not null)
        {
            return Task.FromException<int?>(new SapRequestRejectedException("save stock transfer draft", HttpStatusCode.BadRequest, RefuseWith));
        }

        Saves.Add(((int)args[0]!, (CancellationToken)args[1]!));
        Land();
        return Task.FromResult(SaveReturns);
    }

    private void Land()
    {
        _saved = true;
        foreach (var docEntry in ClosedBySapOnSave)
        {
            TransferRequests[docEntry].DocumentStatus = SapDocumentStatuses.Closed;
        }
    }

    public static SAPStockTransferDraft Closed(SAPStockTransferDraft draft) => new()
    {
        DocEntry = draft.DocEntry,
        DocNum = draft.DocNum,
        DocObjectCode = draft.DocObjectCode,
        FromWarehouse = draft.FromWarehouse,
        ToWarehouse = draft.ToWarehouse,
        DocumentStatus = SapDocumentStatuses.Closed,
        AuthorizationStatus = SapStockTransferAuthorizationStatuses.Without,
        StockTransferLines = draft.StockTransferLines
    };
}

/// <summary>Builds the documents the held-transfer tests talk about.</summary>
internal static class HeldTransfers
{
    /// <summary>An approved, open transfer draft from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static SAPStockTransferDraft Draft(int docEntry, string from, string to, params SAPStockTransferDraftLine[] lines) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry + 30000,
        DocObjectCode = SapDocObjectCodes.StockTransfer,
        DocDate = "2026-11-02T00:00:00Z",
        FromWarehouse = from,
        ToWarehouse = to,
        DocumentStatus = SapDocumentStatuses.Open,
        AuthorizationStatus = SapStockTransferAuthorizationStatuses.Approved,
        StockTransferLines = lines.Length > 0 ? lines.ToList() : [Line(0, "YOG058", 8m, from, to, ("YOG058#M11", 8m))]
    };

    /// <summary>
    /// An approved, open transfer request draft: the same shape, its own object code, and lines with no
    /// batch picks, because a request carries none.
    /// </summary>
    public static SAPStockTransferDraft RequestDraft(int docEntry, string from, string to, params SAPStockTransferDraftLine[] lines)
    {
        var draft = Draft(docEntry, from, to, lines.Length > 0 ? lines : [Line(0, "YOG058", 48m, from, to)]);
        draft.DocObjectCode = SapDocObjectCodes.InventoryTransferRequest;
        return draft;
    }

    /// <summary>The transfer request SAP holds once a request draft is added, carrying the draft's lines.</summary>
    public static InventoryTransferRequest RequestLike(SAPStockTransferDraft draft, int docEntry, int docNum) => new()
    {
        DocEntry = docEntry,
        DocNum = docNum,
        DocDate = "2026-11-11",
        FromWarehouse = draft.FromWarehouse,
        ToWarehouse = draft.ToWarehouse,
        DocumentStatus = SapDocumentStatuses.Open,
        StockTransferLines = (draft.StockTransferLines ?? []).Select(line => new InventoryTransferRequestLine
        {
            LineNum = line.LineNum,
            ItemCode = line.ItemCode,
            Quantity = line.Quantity,
            FromWarehouseCode = line.FromWarehouseCode,
            WarehouseCode = line.WarehouseCode
        }).ToList()
    };

    public static SAPStockTransferDraftLine Line(
        int lineNum,
        string itemCode,
        decimal quantity,
        string from,
        string to,
        params (string Batch, decimal Quantity)[] batches) => new()
        {
            LineNum = lineNum,
            ItemCode = itemCode,
            Quantity = quantity,
            InventoryQuantity = quantity,
            FromWarehouseCode = from,
            WarehouseCode = to,
            BaseType = "Default",
            BatchNumbers = batches.Select(batch => new InventoryTransferLineBatch { BatchNumber = batch.Batch, Quantity = batch.Quantity }).ToList()
        };

    /// <summary>The line, copied from transfer request <paramref name="requestDocEntry"/>.</summary>
    public static SAPStockTransferDraftLine CopiedFrom(this SAPStockTransferDraftLine line, int requestDocEntry)
    {
        line.BaseType = SapStockTransferBaseTypes.InventoryTransferRequest;
        line.BaseEntry = requestDocEntry;
        line.BaseLine = line.LineNum;
        return line;
    }

    public static SAPApprovalRequest Request(int code, int draftEntry, string status = SapApprovalRequestStatuses.Approved) => new()
    {
        Code = code,
        ObjectType = SapObjectTypes.StockTransfer,
        IsDraft = "Y",
        Status = status,
        DraftEntry = draftEntry,
        CurrentStage = 15,
        ApprovalTemplatesID = 72,
        OriginatorID = 48,
        CreationDate = "2026-11-02T00:00:00Z",
        CreationTime = "14:01:00"
    };

    /// <summary>The request as the B1 client leaves it once its draft is added: still approved, naming the transfer.</summary>
    public static SAPApprovalRequest Generated(this SAPApprovalRequest request, int transferDocEntry) => new()
    {
        Code = request.Code,
        ObjectType = request.ObjectType,
        IsDraft = "N",
        Status = request.Status,
        DraftEntry = request.DraftEntry,
        ObjectEntry = transferDocEntry,
        CurrentStage = request.CurrentStage,
        ApprovalTemplatesID = request.ApprovalTemplatesID,
        OriginatorID = request.OriginatorID
    };

    public static InventoryTransfer TransferLike(SAPStockTransferDraft draft, int docEntry, int docNum) => new()
    {
        DocEntry = docEntry,
        DocNum = docNum,
        FromWarehouse = draft.FromWarehouse,
        ToWarehouse = draft.ToWarehouse,
        StockTransferLines = (draft.StockTransferLines ?? []).Select(line => new InventoryTransferLine
        {
            LineNum = line.LineNum,
            ItemCode = line.ItemCode,
            Quantity = line.Quantity,
            FromWarehouseCode = line.FromWarehouseCode,
            WarehouseCode = line.WarehouseCode
        }).ToList()
    };

    public static InventoryTransferRequest OpenRequest(int docEntry, int docNum, string from, string to) => new()
    {
        DocEntry = docEntry,
        DocNum = docNum,
        DocDate = "2026-11-01",
        FromWarehouse = from,
        ToWarehouse = to,
        DocumentStatus = SapDocumentStatuses.Open
    };

    public static Dictionary<string, SapItemManagement> BatchManaged(params string[] itemCodes) =>
        itemCodes.ToDictionary(code => code, code => new SapItemManagement(code, true, false), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every role the application defines, read off <see cref="ApplicationRoles"/>.</summary>
    public static IReadOnlyList<string> RoleNames() => typeof(ApplicationRoles)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToList();
}

/// <summary>Till sales not yet in SAP, per warehouse and item.</summary>
internal sealed class FixedTillClaims(Dictionary<string, Dictionary<string, decimal>>? claims = null) : IUnpostedTillClaims
{
    public Task<IReadOnlyDictionary<string, decimal>> ForWarehouseAsync(string warehouseCode, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, decimal>>(
            claims?.GetValueOrDefault(warehouseCode) ?? new Dictionary<string, decimal>());
}

/// <summary>Records what the app's own approval engine is told about generated requests.</summary>
internal sealed class RecordingTransferApprovals
{
    public List<(int RequestDocEntry, int TransferDocEntry, int TransferDocNum, Guid UserId)> Generated { get; } = [];

    public IInventoryTransferApprovalService AsService() => StubProxy.For<IInventoryTransferApprovalService>((method, args) =>
    {
        if (method.Name == nameof(IInventoryTransferApprovalService.MarkGeneratedAsync) && args![0] is int requestDocEntry)
        {
            Generated.Add((requestDocEntry, (int)args[1]!, (int)args[2]!, (Guid)args[3]!));
            return Task.CompletedTask;
        }

        throw new InvalidOperationException($"{method.Name} was not expected.");
    });
}

/// <summary>A stock check that answers what it is told and remembers that it was asked.</summary>
internal sealed class FixedStockValidation(StockValidationResult answer)
{
    public int Calls { get; private set; }

    public IStockValidationService AsService() => StubProxy.For<IStockValidationService>((method, _) =>
    {
        if (method.Name != nameof(IStockValidationService.ValidateInventoryTransferStockAsync))
        {
            throw new InvalidOperationException($"{method.Name} was not expected.");
        }

        Calls++;
        return Task.FromResult(answer);
    });
}
