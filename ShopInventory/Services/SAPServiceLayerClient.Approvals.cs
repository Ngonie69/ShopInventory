using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using ShopInventory.Common.Security;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The SAP approval-procedure surface: the requests SAP's own approval procedure holds against A/R credit
/// memo drafts, the drafts themselves, their attachments, and the two writes — a decision and the add.
/// </summary>
/// <remarks>
/// A separate file rather than more of <c>SAPServiceLayerClient.cs</c> because nothing here shares a
/// code path with the document reads there: <c>Drafts</c> is a <c>Document</c>-shaped set, but the
/// approval entities are not, and the attachment stream is the client's only binary read. Every call
/// goes through one <see cref="SendSapRequestAsync"/>, which carries the session cookie, retries the
/// transient failures and re-authenticates once on a 401 — the same shape the older methods each spell
/// out by hand.
/// </remarks>
public partial class SAPServiceLayerClient
{
    // Drafts are Document rows, so this is CreditNoteSelect plus the three fields a held draft carries
    // that a posted credit note never needs. Kept separate from CreditNoteSelect on purpose: a field the
    // Drafts set rejects at runtime must not take the credit-note list down with it.
    private const string DraftSelect = "$select=DocEntry,DocNum,DocDate,DocDueDate,CardCode,CardName,NumAtCard,Comments,DocTotal,DocTotalFc,VatSum,DocCurrency,SalesPersonCode,DocumentStatus,Cancelled,AttachmentEntry,AuthorizationStatus,DocObjectCode";
    private const string DraftDetailSelect = DraftSelect + ",DocumentLines";

    private const string ApprovalRequestSelect = "$select=Code,ApprovalTemplatesID,ObjectType,IsDraft,ObjectEntry,Status,Remarks,CurrentStage,OriginatorID,CreationDate,CreationTime,DraftEntry";
    private const string ApprovalRequestDetailSelect = ApprovalRequestSelect + ",ApprovalRequestLines";
    private const string ApprovalStageSelect = "$select=Code,Name,NoOfApproversRequired,ApprovalStageApprovers";
    private const string ApprovalTemplateSelect = "$select=Code,Name,IsActive";
    private const string SapUserSelect = "$select=InternalKey,UserCode,UserName";
    private const string AttachmentSelect = "$select=AbsoluteEntry,Attachments2_Lines";

    /// <summary>Just enough of a credit note to recognise the one an add produced.</summary>
    private const string NewestCreditNoteSelect = "$select=DocEntry,DocNum,DocDate,CardCode,DocTotal,DocCurrency";

    /// <summary>How many draft keys go into one <c>DocEntry eq … or …</c> filter.</summary>
    private const int DraftKeyChunkSize = 20;

    private const string SaveDraftToDocumentOperation = "DraftsService_SaveDraftToDocument";

    /// <summary>
    /// What <c>ApprovalRequestDecision.Remarks</c> holds. Measured against KEFALOS_TEST_3 on
    /// 2026-09-02 — 200 is accepted and 201 is not, and SAP refuses the whole decision with
    /// <c>Value too long in property 'Remarks'</c> rather than truncating it.
    /// </summary>
    internal const int SapDecisionRemarksLength = 200;

    private static readonly FileExtensionContentTypeProvider AttachmentContentTypes = new();

    private static readonly HashSet<string> KnownApprovalRequestStatuses = new(StringComparer.Ordinal)
    {
        SapApprovalRequestStatuses.Pending,
        SapApprovalRequestStatuses.Approved,
        SapApprovalRequestStatuses.NotApproved,
        SapApprovalRequestStatuses.Generated,
        SapApprovalRequestStatuses.GeneratedByAuthorizer,
        SapApprovalRequestStatuses.Cancelled
    };

    private static readonly HashSet<string> KnownApprovalDecisions = new(StringComparer.Ordinal)
    {
        SapApprovalDecisions.Approved,
        SapApprovalDecisions.NotApproved
    };

    #region Reads

    public async Task<(List<SAPApprovalRequest> Items, int TotalCount)> GetCreditNoteApprovalRequestsAsync(
        IReadOnlyCollection<string> sapStatuses,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // The statuses go into the filter as literals, so they are whitelisted rather than escaped.
        var unknown = sapStatuses.Where(status => !KnownApprovalRequestStatuses.Contains(status)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"Unknown SAP approval request status(es): {string.Join(", ", unknown)}", nameof(sapStatuses));
        }

        var statuses = sapStatuses.Distinct(StringComparer.Ordinal).ToList();
        if (statuses.Count == 0)
        {
            throw new ArgumentException("At least one SAP approval request status is required.", nameof(sapStatuses));
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, DocumentListPageSize);
        var skip = (page - 1) * pageSize;

        var statusFilter = string.Join(" or ", statuses.Select(status => $"Status eq '{status}'"));
        var filter = Uri.EscapeDataString($"ObjectType eq '{SapObjectTypes.CreditNote}' and ({statusFilter})");
        var listUrl = $"ApprovalRequests?$filter={filter}&{ApprovalRequestSelect}&$orderby=Code desc&$top={pageSize}&$skip={skip}";
        var countUrl = $"ApprovalRequests/$count?$filter={filter}";

        var pageResult = await ReadSapJsonAsync<SAPResponse<SAPApprovalRequest>>(
            listUrl, $"list credit note approval requests (page {page})", cancellationToken, pageSize);
        var total = await ReadSapCountAsync(countUrl, "count credit note approval requests", cancellationToken);

        return (pageResult?.Value ?? [], total);
    }

    public Task<SAPApprovalRequest?> GetApprovalRequestAsync(int code, CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<SAPApprovalRequest>(
            $"ApprovalRequests({code})?{ApprovalRequestDetailSelect}", $"read approval request {code}", cancellationToken);

    public async Task<List<SAPCreditNote>> GetCreditNoteDraftsAsync(
        IReadOnlyCollection<int> docEntries,
        CancellationToken cancellationToken = default)
    {
        var keys = docEntries.Where(key => key > 0).Distinct().OrderByDescending(key => key).ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        await EnsureAuthenticatedAsync(cancellationToken);

        var drafts = new List<SAPCreditNote>(keys.Count);
        foreach (var chunk in keys.Chunk(DraftKeyChunkSize))
        {
            var keyFilter = string.Join(" or ", chunk.Select(key => $"DocEntry eq {key}"));
            drafts.AddRange(await ReadDocumentPagesAsync<SAPCreditNote>(
                "Drafts",
                $"DocObjectCode eq '{SapDocObjectCodes.CreditNotes}' and ({keyFilter})",
                DraftSelect,
                $"read {chunk.Length} credit note draft(s)",
                cancellationToken,
                NoDocumentListCeiling));
        }

        return drafts;
    }

    public Task<SAPCreditNote?> GetCreditNoteDraftAsync(int docEntry, CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<SAPCreditNote>($"Drafts({docEntry})?{DraftDetailSelect}", $"read draft {docEntry}", cancellationToken);

    public async Task<SAPCreditNote?> GetNewestCreditNoteForCustomerAsync(
        string cardCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cardCode))
        {
            return null;
        }

        var filter = Uri.EscapeDataString($"CardCode eq '{EscapeODataStringLiteral(cardCode.Trim())}'");
        var page = await ReadSapJsonAsync<SAPResponse<SAPCreditNote>>(
            $"CreditNotes?$filter={filter}&{NewestCreditNoteSelect}&$orderby=DocEntry desc&$top=1",
            $"read the newest credit note for {cardCode}",
            cancellationToken,
            pageSize: 1);

        return page?.Value?.FirstOrDefault();
    }

    public Task<SAPAttachment?> GetAttachmentAsync(int absoluteEntry, CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<SAPAttachment>(
            $"Attachments2({absoluteEntry})?{AttachmentSelect}", $"read attachment {absoluteEntry}", cancellationToken);

    public Task<SAPUser?> GetSapUserAsync(int internalKey, CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<SAPUser>($"Users({internalKey})?{SapUserSelect}", $"read SAP user {internalKey}", cancellationToken);

    public async Task<SAPUser?> GetSapUserByCodeAsync(string userCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userCode))
        {
            return null;
        }

        var filter = Uri.EscapeDataString($"UserCode eq '{EscapeODataStringLiteral(userCode.Trim())}'");
        var page = await ReadSapJsonAsync<SAPResponse<SAPUser>>(
            $"Users?$filter={filter}&{SapUserSelect}&$top=1", $"look up SAP user '{userCode}'", cancellationToken, pageSize: 1);

        return page?.Value?.FirstOrDefault();
    }

    public Task<SAPApprovalTemplate?> GetApprovalTemplateAsync(int code, CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<SAPApprovalTemplate>(
            $"ApprovalTemplates({code})?{ApprovalTemplateSelect}", $"read approval template {code}", cancellationToken);

    public Task<SAPApprovalStage?> GetApprovalStageAsync(int code, CancellationToken cancellationToken = default)
        => ReadSapJsonAsync<SAPApprovalStage>(
            $"ApprovalStages({code})?{ApprovalStageSelect}", $"read approval stage {code}", cancellationToken);

    #endregion

    #region Writes

    public async Task SubmitApprovalDecisionAsync(
        int code,
        string? approverUserName,
        string? approverPassword,
        string sapDecision,
        string? remarks,
        CancellationToken cancellationToken = default)
    {
        if (!KnownApprovalDecisions.Contains(sapDecision))
        {
            throw new ArgumentException($"'{sapDecision}' is not a SAP approval decision.", nameof(sapDecision));
        }

        if (remarks is { Length: > SapDecisionRemarksLength })
        {
            throw new ArgumentException(
                $"SAP holds at most {SapDecisionRemarksLength} characters of decision remarks; the caller must truncate.",
                nameof(remarks));
        }

        var decision = new JsonObject
        {
            ["Status"] = sapDecision
        };

        // Naming nobody records the decision as the session user, which is what the default
        // configuration wants and what keeps a password off the wire entirely.
        if (!string.IsNullOrWhiteSpace(approverUserName))
        {
            decision["ApproverUserName"] = approverUserName;

            if (approverPassword is not null)
            {
                decision["ApproverPassword"] = approverPassword;
            }
        }

        if (!string.IsNullOrWhiteSpace(remarks))
        {
            decision["Remarks"] = remarks;
        }

        var payload = new JsonObject
        {
            ["ApprovalRequestDecisions"] = new JsonArray(decision)
        };
        var json = payload.ToJsonString();

        // The payload is never logged: it may carry the approver's password.
        var approverLabel = string.IsNullOrWhiteSpace(approverUserName) ? _settings.Username : approverUserName;
        var operation = $"record {sapDecision} on approval request {code} as {approverLabel}";
        _logger.LogInformation("Recording {Decision} on SAP approval request {Code} as {Approver}", sapDecision, code, approverLabel);

        using var response = await SendSapRequestAsync(
            () => CreateSapJsonRequest(HttpMethod.Patch, $"ApprovalRequests({code})", json),
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        await EnsureSapSuccessAsync(response, operation, cancellationToken);
    }

    public async Task<int?> SaveDraftToDocumentAsync(int draftDocEntry, CancellationToken cancellationToken = default)
    {
        var json = new JsonObject
        {
            ["Document"] = new JsonObject { ["DocEntry"] = draftDocEntry }
        }.ToJsonString();

        var operation = $"save draft {draftDocEntry} to document";
        _logger.LogInformation("Saving SAP draft {DraftEntry} to its document", draftDocEntry);

        using var response = await SendSapRequestAsync(
            () => CreateSapJsonRequest(HttpMethod.Post, SaveDraftToDocumentOperation, json),
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        await EnsureSapSuccessAsync(response, operation, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ReadDocEntry(body);
    }

    /// <summary>The <c>DocEntry</c> of a document body, or null when the answer is not one.</summary>
    private static int? ReadDocEntry(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("DocEntry", out var docEntry)
                && docEntry.ValueKind == JsonValueKind.Number
                && docEntry.TryGetInt32(out var value)
                && value > 0)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Not JSON; SAP said nothing about the document it made.
        }

        return null;
    }

    #endregion

    #region Attachment bytes

    public async Task<SapAttachmentDownload?> DownloadAttachmentAsync(
        int absoluteEntry,
        string fileNameWithExtension,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithExtension))
        {
            throw new ArgumentException("A file name is required.", nameof(fileNameWithExtension));
        }

        // The file name is an OData string literal inside the query: quotes doubled, then URL-encoded.
        var literal = Uri.EscapeDataString(EscapeODataStringLiteral(fileNameWithExtension));
        var url = $"Attachments2({absoluteEntry})/$value?filename='{literal}'";
        var operation = $"download attachment {absoluteEntry} '{fileNameWithExtension}'";

        var response = await SendSapRequestAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            operation,
            cancellationToken);

        // Deliberately no 404-to-null shortcut here, unlike the metadata reads. SAP answers the
        // stream with 404 and a real message when its own attachments folder is not mounted
        // ("Fail to get the LINUX mount point for AttachmentsFolderPath"), and reporting that as
        // "there is no such file" sends somebody hunting for a document that exists. The caller
        // has already established that the line is there; whatever SAP says now is the reason.
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                await EnsureSapSuccessAsync(response, operation, cancellationToken);
            }
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType)
            || string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            contentType = ResolveAttachmentContentType(fileNameWithExtension);
        }

        return new SapAttachmentDownload(new ResponseOwningStream(stream, response), contentType, fileNameWithExtension);
    }

    public async Task<SapAttachmentDownload?> ReadAttachmentFromShareAsync(
        SAPAttachmentLine line,
        CancellationToken cancellationToken = default)
    {
        var attachmentsPath = _settings.AttachmentsPath?.Trim();
        if (string.IsNullOrWhiteSpace(attachmentsPath))
        {
            throw new InvalidOperationException("SAP:AttachmentsPath is not configured, so attachments cannot be read from the share.");
        }

        var fileName = line.FullFileName;
        if (!IsPlainFileName(fileName))
        {
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(line));
        }

        // SAP copies every attached file into the attachments folder; SourcePath is where the person
        // picked it from, which may be their own desktop, so the folder is always the configured one.
        var fullPath = Path.Combine(attachmentsPath, fileName);

        // Read the whole file while the share connection is held: a temporary connection may not
        // outlive its using block, and a half-read stream across a dropped mapping is worse than a
        // few megabytes in memory.
        using var share = ConnectToSapShareIfNeeded(attachmentsPath);

        // An unreachable share and an absent file both answer false from File.Exists, and the two
        // are acted on by different people: one is a server or credentials problem, the other means
        // SAP has a line for a file its own folder no longer holds. Name the folder first.
        if (!Directory.Exists(attachmentsPath))
        {
            throw new IOException(
                $"The SAP attachments folder '{attachmentsPath}' could not be reached from this server.");
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        var buffer = new MemoryStream();
        await using (var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            await file.CopyToAsync(buffer, cancellationToken);
        }

        buffer.Position = 0;
        return new SapAttachmentDownload(buffer, ResolveAttachmentContentType(fileName), fileName);
    }

    /// <summary>
    /// Whether a name SAP handed back is a bare file name, safe to combine with the attachments folder.
    /// </summary>
    /// <remarks>
    /// Both separators are rejected on every platform rather than deferring to
    /// <see cref="Path.GetFileName(string)"/> alone, which only understands the separators of the
    /// platform it is running on: on Linux a backslash is an ordinary character, so
    /// <c>..\..\secrets.txt</c> came back unchanged and passed a guard that catches it on Windows.
    /// A value that reaches here comes from SAP, so it is not this application's to trust either way.
    /// </remarks>
    private static bool IsPlainFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || fileName.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (fileName is "." or ".." || Path.IsPathRooted(fileName))
        {
            return false;
        }

        // The platform's own opinion, as a backstop for anything the checks above do not name.
        return fileName == Path.GetFileName(fileName);
    }

    private static string ResolveAttachmentContentType(string fileName)
        => AttachmentContentTypes.TryGetContentType(fileName, out var contentType)
            ? contentType
            : "application/octet-stream";

    /// <summary>
    /// A read-only stream that disposes the SAP response it came from, so a controller's
    /// <c>File(stream, …)</c> — which disposes only the stream — releases the connection too.
    /// </summary>
    private sealed class ResponseOwningStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => inner.CopyToAsync(destination, bufferSize, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }

    #endregion

    #region Request plumbing

    /// <summary>
    /// Sends one request with the session cookie, retrying transient failures and re-authenticating
    /// once on a 401. <paramref name="requestFactory"/> is invoked per attempt, so it must read
    /// <c>_sessionId</c> when called rather than capture it.
    /// </summary>
    private async Task<HttpResponseMessage> SendSapRequestAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        string operation,
        CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var currentSession = _sessionId;

        var response = await SendSapRequestWithTransientRetryAsync(
            _httpClient, requestFactory, completionOption, operation, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        await HandleAuthFailureAsync(currentSession, cancellationToken);

        return await SendSapRequestWithTransientRetryAsync(
            _httpClient, requestFactory, completionOption, $"{operation} after SAP re-authentication", cancellationToken);
    }

    /// <summary>A JSON GET; null on a 404, a <see cref="SapRequestRejectedException"/> on any other failure.</summary>
    private async Task<T?> ReadSapJsonAsync<T>(
        string url,
        string operation,
        CancellationToken cancellationToken,
        int? pageSize = null)
    {
        using var response = await SendSapRequestAsync(
            () =>
            {
                var request = CreateSapJsonRequest(HttpMethod.Get, url);
                if (pageSize is not null)
                {
                    request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
                }

                return request;
            },
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        await EnsureSapSuccessAsync(response, operation, cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content);
    }

    private async Task<int> ReadSapCountAsync(string url, string operation, CancellationToken cancellationToken)
    {
        using var response = await SendSapRequestAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return request;
            },
            HttpCompletionOption.ResponseContentRead,
            operation,
            cancellationToken);

        await EnsureSapSuccessAsync(response, operation, cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return int.TryParse(content.Trim(), out var count) ? count : 0;
    }

    /// <summary>
    /// Turns a non-success answer into <see cref="SapRequestRejectedException"/> carrying SAP's own
    /// message. The body is logged sanitised; it never contains a password, but it does contain
    /// whatever SAP chose to echo.
    /// </summary>
    private async Task EnsureSapSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = ExtractSAPErrorMessage(body) ?? (string.IsNullOrWhiteSpace(body) ? response.StatusCode.ToString() : body);

        _logger.LogError(
            "Failed to {Operation}: {StatusCode} - {Error}",
            operation,
            response.StatusCode,
            SensitiveDataSanitizer.SanitizeForLog(message));

        throw new SapRequestRejectedException(operation, response.StatusCode, message);
    }

    #endregion
}
