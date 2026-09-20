using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Common.Security;
using ShopInventory.Common.Validation;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The Goods Issue surface: the one document this client posts that takes stock off SAP's books
/// without a business partner on the other side of it.
/// </summary>
/// <remarks>
/// <para>
/// A separate file rather than more of <c>SAPServiceLayerClient.cs</c>, which is already seventeen
/// thousand lines. Everything here is shared with that file through the class being partial — the
/// session, <see cref="EnsureAuthenticatedAsync"/>, the date and error helpers.
/// </para>
/// <para>
/// <c>InventoryGenExits</c> is a <c>Document</c>-shaped entity set, the same shape as
/// <c>Invoices</c>, so this reads and writes <c>DocumentLines</c>. It is emphatically not shaped like
/// <c>StockTransfers</c>, whose <c>StockTransfer</c> type carries a warehouse at each end; the
/// resemblance between a transfer and a write-off is physical, not structural.
/// </para>
/// </remarks>
public partial class SAPServiceLayerClient
{
    private const string GoodsIssueSelect =
        "$select=DocEntry,DocNum,DocDate,Comments,JournalMemo,Reference2,DocTotal,DocumentStatus,Cancelled";

    private const string GoodsIssueDetailSelect = GoodsIssueSelect + ",DocumentLines";

    /// <summary>
    /// The goods-issue line table, whose own <c>Reasons</c> user field — if this company database
    /// defines one — carries the reason a write-off line exists.
    /// </summary>
    private const string GoodsIssueReasonLineTable = "IGE1";

    /// <summary>What SAP's document <c>Comments</c> field holds.</summary>
    private const int SapCommentsMaxLength = 254;

    public Task<IReadOnlyList<SapDocumentLineReason>> GetGoodsIssueLineReasonsAsync(
        CancellationToken cancellationToken = default)
    {
        return ReadLineReasonsAsync(
            GoodsIssueReasonLineTable,
            ReturnReasonFieldName,
            "goods issue",
            cancellationToken);
    }

    public async Task<GoodsIssue> CreateGoodsIssueAsync(
        CreateGoodsIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warehouse = request.WarehouseCode?.Trim();
        if (string.IsNullOrWhiteSpace(warehouse))
        {
            throw new ArgumentException("Warehouse code is required");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new ArgumentException("At least one line item is required");
        }

        await EnsureAuthenticatedAsync(cancellationToken);

        // Whether each item is batch- or serial-managed, so a line that SAP would refuse for an
        // incomplete selection is refused here instead, with a message naming the line. One bulk
        // read, and a cached one: a write-off of twenty lines must not be twenty round-trips.
        var itemCodes = request.Lines
            .Select(line => UomQuantityValidation.NormalizeItemCode(line.ItemCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = await GetItemsByCodesAsync(itemCodes, cancellationToken);

        ValidateGoodsIssueLines(request, items);

        var docDate = FormatSapDocumentDate(
            ResolveSapDocumentDate(request.DocDate, "DocDate", GetCurrentSapBusinessDate()));

        // Asked before the payload is built, and only when a line actually carries a reason: naming
        // a user field the company database does not define is refused by SAP, and the answer is a
        // property of the database rather than of this document, so it is read once per process.
        var canSendReason = request.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Reason))
            && await PrimeGoodsIssueReasonFieldAsync(cancellationToken);

        var payload = BuildGoodsIssuePayload(request, warehouse, docDate, canSendReason);
        var json = payload.ToJsonString();

        _logger.LogInformation(
            "Creating SAP goods issue out of {Warehouse} with {LineCount} line(s), DocDate {DocDate}, reference {Reference}",
            warehouse,
            request.Lines.Count,
            docDate,
            SensitiveDataSanitizer.SanitizeIdentifierForLog(request.SapReference));

        var currentSession = _sessionId;
        var response = await _httpClient.SendAsync(
            CreateSapJsonRequest(HttpMethod.Post, "InventoryGenExits", json),
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // SAP answered the first attempt with a 401, so that attempt created nothing, and a
            // failure to log in again happens strictly before the second attempt is sent.
            await BeforeSendAsync(HandleAuthFailureAsync(currentSession, cancellationToken));

            response = await _httpClient.SendAsync(
                CreateSapJsonRequest(HttpMethod.Post, "InventoryGenExits", json),
                cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowGoodsIssueRejectionAsync(response, warehouse, docDate, cancellationToken);
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var created = JsonSerializer.Deserialize<GoodsIssue>(responseContent);

        if (created is null)
        {
            // Deserialization failing happens *after* SAP committed the document, so this must not
            // look like a rejection: the stock has already left.
            throw new Exception("Failed to deserialize the created goods issue");
        }

        _logger.LogInformation(
            "SAP goods issue created out of {Warehouse}: DocEntry {DocEntry}, DocNum {DocNum}",
            warehouse, created.DocEntry, created.DocNum);

        return created;
    }

    public async Task<GoodsIssue?> GetGoodsIssueByDocEntryAsync(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        try
        {
            var json = await GetSapJsonAsync(
                $"InventoryGenExits({docEntry})?{GoodsIssueDetailSelect}",
                $"goods issue {docEntry}",
                cancellationToken);

            return JsonSerializer.Deserialize<GoodsIssue>(json);
        }
        catch (Exception exception) when (IsSapNotFound(exception))
        {
            return null;
        }
    }

    public async Task<GoodsIssue?> GetGoodsIssueByReferenceAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        await EnsureAuthenticatedAsync(cancellationToken);

        // Doubled rather than stripped: a reference is derived from a caller's idempotency key, and
        // silently altering it here would look the document up under a name it was never filed
        // under, which reads as "no document" and posts the stock a second time.
        var literal = reference.Trim().Replace("'", "''", StringComparison.Ordinal);
        var filter = Uri.EscapeDataString($"Reference2 eq '{literal}'");

        var json = await GetSapJsonAsync(
            $"InventoryGenExits?{GoodsIssueDetailSelect}&$filter={filter}&$orderby=DocEntry desc&$top=1",
            "goods issue by reference",
            cancellationToken);

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("value", out var rows) || rows.GetArrayLength() == 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<GoodsIssue>(rows[0].GetRawText());
    }

    /// <summary>
    /// Builds the document SAP is sent.
    /// </summary>
    /// <remarks>
    /// What is absent matters as much as what is present. No <c>CardCode</c>, because a goods issue
    /// is not a business-partner document. No <c>AccountCode</c>, so SAP's own item and warehouse
    /// G/L determination charges the issue exactly as it would for one keyed into B1 by hand. And no
    /// price, so SAP values the lines at the items' cost — a price sent from here would decide what
    /// the write-off is worth, which is not this system's to decide.
    /// </remarks>
    private static JsonObject BuildGoodsIssuePayload(
        CreateGoodsIssueRequest request,
        string warehouse,
        string docDate,
        bool canSendReason)
    {
        var lines = new JsonArray();

        foreach (var line in request.Lines)
        {
            var payloadLine = new JsonObject
            {
                ["ItemCode"] = UomQuantityValidation.NormalizeItemCode(line.ItemCode),
                ["Quantity"] = line.Quantity,
                ["WarehouseCode"] = warehouse
            };

            if (!string.IsNullOrWhiteSpace(line.UoMCode))
            {
                payloadLine["UoMCode"] = line.UoMCode.Trim();
            }

            var reason = NormalizeReturnReason(line.Reason);
            if (reason is not null && canSendReason)
            {
                payloadLine[$"U_{ReturnReasonFieldName}"] = reason;
            }

            if (line.BatchNumbers is { Count: > 0 })
            {
                var batches = new JsonArray();
                foreach (var batch in line.BatchNumbers)
                {
                    batches.Add(new JsonObject
                    {
                        ["BatchNumber"] = batch.BatchNumber,
                        ["Quantity"] = batch.Quantity
                    });
                }

                payloadLine["BatchNumbers"] = batches;
            }

            if (line.SerialNumbers is { Count: > 0 })
            {
                var serials = new JsonArray();
                foreach (var serial in line.SerialNumbers)
                {
                    var entry = new JsonObject
                    {
                        ["InternalSerialNumber"] = serial.InternalSerialNumber,
                        ["Quantity"] = 1
                    };

                    if (serial.SystemSerialNumber.HasValue)
                    {
                        entry["SystemSerialNumber"] = serial.SystemSerialNumber.Value;
                    }

                    serials.Add(entry);
                }

                payloadLine["SerialNumbers"] = serials;
            }

            lines.Add(payloadLine);
        }

        var payload = new JsonObject
        {
            ["DocDate"] = docDate,
            ["DocumentLines"] = lines
        };

        if (!string.IsNullOrWhiteSpace(request.Comments))
        {
            payload["Comments"] = Truncate(request.Comments.Trim(), SapCommentsMaxLength);
        }

        if (!string.IsNullOrWhiteSpace(request.JournalMemo))
        {
            // SAP caps JournalMemo well below Comments; the memo is what shows on the journal entry.
            payload["JournalMemo"] = Truncate(request.JournalMemo.Trim(), 50);
        }

        if (!string.IsNullOrWhiteSpace(request.SapReference))
        {
            payload["Reference2"] = Truncate(request.SapReference.Trim(), 100);
        }

        return payload;
    }

    /// <summary>
    /// Refuses a line SAP would refuse, before anything is sent.
    /// </summary>
    /// <remarks>
    /// The batch rule is the one worth knowing: SAP rejects the <em>whole document</em> when a
    /// batch-managed line carries no complete selection, so one unallocated line out of twenty loses
    /// the lot. This client does not allocate for the caller the way the transfer path does — a
    /// write-off is a count of specific physical stock, so guessing which batch was broken would be
    /// inventing the evidence.
    /// </remarks>
    private static void ValidateGoodsIssueLines(
        CreateGoodsIssueRequest request,
        IReadOnlyDictionary<string, Item> items)
    {
        var errors = new List<string>();

        for (var index = 0; index < request.Lines.Count; index++)
        {
            var line = request.Lines[index];
            var itemCode = UomQuantityValidation.NormalizeItemCode(line.ItemCode);

            if (string.IsNullOrWhiteSpace(itemCode))
            {
                errors.Add($"Line {index + 1}: Item code is required");
                continue;
            }

            if (line.Quantity <= 0)
            {
                errors.Add($"Line {index + 1}: Quantity must be greater than zero (current: {line.Quantity})");
            }

            errors.AddRange(DescribeIncompleteLineSelection(
                index,
                itemCode,
                line.Quantity,
                line.BatchNumbers?.Select(batch => (batch.BatchNumber, batch.Quantity)),
                line.SerialNumbers?.Select(serial => serial.InternalSerialNumber)));

            // An item code the item master does not answer for is left alone rather than refused:
            // "not read" is not "not managed", and the transfer path makes the same distinction.
            if (!items.TryGetValue(itemCode, out var item) || item is null)
            {
                continue;
            }

            if (IsSapManaged(item.ManageBatchNumbers) && line.BatchNumbers is not { Count: > 0 })
            {
                errors.Add(
                    $"Line {index + 1}: item {itemCode} is batch-managed, so the batches the units come out of "
                    + "must be named. SAP refuses the whole document when a batch-managed line carries no selection.");
            }

            if (IsSapManaged(item.ManageSerialNumbers) && line.SerialNumbers is not { Count: > 0 })
            {
                errors.Add(
                    $"Line {index + 1}: item {itemCode} is serial-managed, so each unit's serial number must be "
                    + "named. SAP refuses the whole document when a serial-managed line carries no selection.");
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors));
        }
    }

    /// <summary>
    /// Turns SAP's refusal into the exception the callers act on.
    /// </summary>
    /// <remarks>
    /// <see cref="SapRequestRejectedException"/> rather than a bare exception, and the type is the
    /// point: SAP answered, and the answer was no, so the document definitively does not exist and a
    /// caller may safely post again. <c>SapFailureClassifier.DefinitelyNotCommitted</c> reads that
    /// distinction, and a bare exception here would be indistinguishable from a deserialize failure,
    /// which happens after SAP has already moved the stock.
    /// </remarks>
    private async Task ThrowGoodsIssueRejectionAsync(
        HttpResponseMessage response,
        string warehouse,
        string docDate,
        CancellationToken cancellationToken)
    {
        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var sapError = ExtractSAPErrorMessage(errorContent);
        var sanitized = SensitiveDataSanitizer.SanitizeForLog(sapError ?? errorContent);

        if (IsPostingPeriodDateError(errorContent, sapError))
        {
            _logger.LogWarning(
                "SAP rejected the goods issue date DocDate={DocDate} out of {Warehouse}: {SapError}",
                docDate, warehouse, sanitized);

            throw new SapPostingPeriodException(
                $"SAP rejected the write-off because DocDate={docDate} is outside the configured posting period. "
                + $"SAP error: {sanitized}",
                docDate,
                sanitized);
        }

        _logger.LogError(
            "Failed to create the goods issue out of {Warehouse}: {StatusCode} - {Error}",
            warehouse, response.StatusCode, sanitized);

        if (errorContent.Contains("-4014", StringComparison.Ordinal)
            || errorContent.Contains("complete selection of batch", StringComparison.OrdinalIgnoreCase))
        {
            throw new SapRequestRejectedException(
                "create the goods issue",
                response.StatusCode,
                "SAP refused this write-off because a batch or serial selection does not account for the whole "
                + $"line quantity. SAP error: {sanitized}");
        }

        if (errorContent.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("negative", StringComparison.OrdinalIgnoreCase))
        {
            throw new SapRequestRejectedException(
                "create the goods issue",
                response.StatusCode,
                $"SAP refused this write-off: warehouse {warehouse} does not hold enough stock to issue. "
                + $"SAP error: {sanitized}");
        }

        throw new SapRequestRejectedException("create the goods issue", response.StatusCode, sanitized);
    }

    private const string GoodsIssueReasonFieldCacheKey = "SAP_GoodsIssueReasonFieldExists";

    /// <summary>
    /// Establishes whether a write-off reason can be sent to SAP at all, so that
    /// <see cref="BuildGoodsIssuePayload"/> omits the reason field when the company database does not
    /// define it rather than having SAP refuse the document for naming an unknown property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cached, including the negative answer, because it is a property of the company database rather
    /// than of a document. In the injected cache rather than a static field: the cache is a singleton
    /// in the running application, which is the lifetime wanted, and it is per-instance under test,
    /// so one test establishing that the field exists cannot decide the next test's answer.
    /// </para>
    /// <para>
    /// An unreadable answer is taken as "no". That is the safe direction: a write-off that records its
    /// reason only locally is a far smaller loss than a write-off SAP refuses outright.
    /// </para>
    /// </remarks>
    private async Task<bool> PrimeGoodsIssueReasonFieldAsync(CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(GoodsIssueReasonFieldCacheKey, out bool cached))
        {
            return cached;
        }

        bool exists;
        try
        {
            var reasons = await GetGoodsIssueLineReasonsAsync(cancellationToken);
            exists = reasons.Count > 0;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not read whether {Table} defines a {Field} user field; write-off reasons will be kept locally only",
                GoodsIssueReasonLineTable, ReturnReasonFieldName);
            exists = false;
        }

        _memoryCache.Set(GoodsIssueReasonFieldCacheKey, exists, TimeSpan.FromHours(6));
        return exists;
    }

    private static bool IsSapManaged(string? flag) =>
        string.Equals(flag, "tYES", StringComparison.OrdinalIgnoreCase);

    private static bool IsSapNotFound(Exception exception) =>
        exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase);
}
