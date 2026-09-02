using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The approval-procedure calls of <see cref="SAPServiceLayerClient"/>, driven against a fake Service
/// Layer that records what was sent. The URLs and payloads are the contract with SAP, and nothing else
/// checks them before a live run.
/// </summary>
/// <remarks>
/// In the client's collection because the session lives in statics; see
/// <see cref="SapServiceLayerClientCollection"/>.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class SapApprovalClientTests
{
    [Fact]
    public async Task The_request_list_filters_on_the_credit_memo_type_and_the_given_statuses_and_pages_by_code()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Method == HttpMethod.Get && r.Path.EndsWith("/ApprovalRequests"),
            _ => Json("""{"value":[{"Code":5,"ObjectType":"14","Status":"arsPending","DraftEntry":77}]}"""));
        sap.On(r => r.Path.EndsWith("/ApprovalRequests/$count"), _ => Text("12"));
        var client = CreateClient(sap);

        var (items, total) = await client.GetCreditNoteApprovalRequestsAsync(
            [SapApprovalRequestStatuses.Pending, SapApprovalRequestStatuses.Approved], page: 2, pageSize: 5);

        Assert.Equal(12, total);
        Assert.Equal(5, Assert.Single(items).Code);

        var list = Assert.Single(sap.Requests, r => r.Path.EndsWith("/ApprovalRequests"));
        var query = Uri.UnescapeDataString(list.Query);
        Assert.Contains("$filter=ObjectType eq '14' and (Status eq 'arsPending' or Status eq 'arsApproved')", query);
        Assert.Contains("$select=Code,ApprovalTemplatesID,ObjectType,IsDraft,ObjectEntry,Status,Remarks,CurrentStage,OriginatorID,CreationDate,CreationTime,DraftEntry&", query);
        Assert.Contains("$orderby=Code desc&$top=5&$skip=5", query);
        Assert.Equal("odata.maxpagesize=5", list.Prefer);

        var count = Assert.Single(sap.Requests, r => r.Path.EndsWith("/$count"));
        Assert.Contains("ObjectType eq '14' and (Status eq 'arsPending' or Status eq 'arsApproved')", Uri.UnescapeDataString(count.Query));
    }

    [Fact]
    public async Task An_unknown_status_or_decision_is_refused_before_anything_is_sent()
    {
        var sap = new FakeServiceLayer();
        var client = CreateClient(sap);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetCreditNoteApprovalRequestsAsync(["arsPending", "bogus"], 1, 5));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitApprovalDecisionAsync(5, "manager", null, SapApprovalDecisions.Pending, null));

        // SAP refuses the whole decision when the remarks are too long, so an over-long one must
        // never leave here: the caller truncates, and getting that wrong is the caller's bug.
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SubmitApprovalDecisionAsync(
                5, null, null, SapApprovalDecisions.Approved, new string('x', 201)));

        Assert.Empty(sap.Requests);
    }

    [Fact]
    public async Task A_decision_is_a_patch_naming_the_approver_and_leaves_a_null_password_out()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Method == HttpMethod.Patch && r.Path.EndsWith("/ApprovalRequests(5)"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(sap);

        await client.SubmitApprovalDecisionAsync(5, "manager", null, SapApprovalDecisions.Approved, "Approved in ShopInventory by ngoni");

        var patch = Assert.Single(sap.Requests);
        Assert.Equal(HttpMethod.Patch, patch.Method);
        using var body = JsonDocument.Parse(patch.Body!);
        var decision = Assert.Single(body.RootElement.GetProperty("ApprovalRequestDecisions").EnumerateArray());
        Assert.Equal("manager", decision.GetProperty("ApproverUserName").GetString());
        Assert.Equal("ardApproved", decision.GetProperty("Status").GetString());
        Assert.Equal("Approved in ShopInventory by ngoni", decision.GetProperty("Remarks").GetString());
        Assert.False(decision.TryGetProperty("ApproverPassword", out _), "a null password must be omitted, not sent as null");
    }

    /// <summary>
    /// Measured against KEFALOS_TEST_3 on 2026-09-02: a decision naming no approver is recorded as the
    /// session user and needs no password, while naming one without a password is refused outright.
    /// The default configuration therefore puts no credential on the wire at all.
    /// </summary>
    [Fact]
    public async Task A_decision_that_names_nobody_is_the_session_users_and_carries_no_credential()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Method == HttpMethod.Patch, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(sap);

        await client.SubmitApprovalDecisionAsync(5, null, null, SapApprovalDecisions.Approved, "Approved by ngoni");

        using var body = JsonDocument.Parse(Assert.Single(sap.Requests).Body!);
        var decision = Assert.Single(body.RootElement.GetProperty("ApprovalRequestDecisions").EnumerateArray());
        Assert.Equal("ardApproved", decision.GetProperty("Status").GetString());
        Assert.Equal("Approved by ngoni", decision.GetProperty("Remarks").GetString());
        Assert.False(decision.TryGetProperty("ApproverUserName", out _));
        Assert.False(decision.TryGetProperty("ApproverPassword", out _));
    }

    [Fact]
    public async Task A_decision_carries_the_password_when_one_is_given()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Method == HttpMethod.Patch, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(sap);

        await client.SubmitApprovalDecisionAsync(5, "approver", "s3cret", SapApprovalDecisions.NotApproved, null);

        using var body = JsonDocument.Parse(Assert.Single(sap.Requests).Body!);
        var decision = Assert.Single(body.RootElement.GetProperty("ApprovalRequestDecisions").EnumerateArray());
        Assert.Equal("s3cret", decision.GetProperty("ApproverPassword").GetString());
        Assert.Equal("ardNotApproved", decision.GetProperty("Status").GetString());
        Assert.False(decision.TryGetProperty("Remarks", out _));
    }

    [Fact]
    public async Task A_refused_decision_surfaces_saps_own_message_as_a_rejection()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Method == HttpMethod.Patch,
            _ => Json("""{"error":{"code":-5002,"message":{"lang":"en-us","value":"User is not an approver of this stage"}}}""", HttpStatusCode.BadRequest));
        var client = CreateClient(sap);

        var rejection = await Assert.ThrowsAsync<SapRequestRejectedException>(
            () => client.SubmitApprovalDecisionAsync(5, "manager", null, SapApprovalDecisions.Approved, null));

        Assert.Equal("User is not an approver of this stage", rejection.SapMessage);
        Assert.Equal(HttpStatusCode.BadRequest, rejection.StatusCode);
        Assert.Contains("approval request 5", rejection.Operation);
    }

    [Fact]
    public async Task Saving_a_draft_posts_the_unbound_operation_and_reads_the_doc_entry_when_the_answer_names_it()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/DraftsService_SaveDraftToDocument"),
            _ => Json("""{"DocEntry":9001,"DocNum":9001}""", HttpStatusCode.Created));
        var client = CreateClient(sap);

        var created = await client.SaveDraftToDocumentAsync(77);

        Assert.Equal(9001, created);
        var post = Assert.Single(sap.Requests);
        Assert.Equal("/b1s/v1/DraftsService_SaveDraftToDocument", post.Path);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(77, body.RootElement.GetProperty("Document").GetProperty("DocEntry").GetInt32());
    }

    [Fact]
    public async Task Saving_a_draft_returns_null_when_sap_answers_with_no_content()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Method == HttpMethod.Post, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(sap);

        Assert.Null(await client.SaveDraftToDocumentAsync(77));
    }

    [Fact]
    public async Task An_attachment_streams_from_value_with_the_file_name_quoted_and_its_quotes_doubled()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 test");
        var sap = new FakeServiceLayer();
        sap.On(r => r.Path.EndsWith("/Attachments2(5)/$value"), _ => Bytes(bytes, "application/pdf"));
        var client = CreateClient(sap);

        using var download = await client.DownloadAttachmentAsync(5, "O'Brien note.pdf");

        Assert.NotNull(download);
        Assert.Equal("application/pdf", download.ContentType);
        Assert.Equal("O'Brien note.pdf", download.FileName);
        using var read = new MemoryStream();
        await download.Content.CopyToAsync(read);
        Assert.Equal(bytes, read.ToArray());

        var get = Assert.Single(sap.Requests);
        Assert.Equal("?filename='O''Brien note.pdf'", Uri.UnescapeDataString(get.Query));
        Assert.DoesNotContain(" ", get.Query);
    }

    [Fact]
    public async Task An_attachment_with_no_useful_content_type_is_typed_by_its_extension()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Path.Contains("/$value"), _ => Bytes([1, 2, 3], "application/octet-stream"));
        var client = CreateClient(sap);

        using var download = await client.DownloadAttachmentAsync(5, "photo.jpg");

        Assert.Equal("image/jpeg", download!.ContentType);
    }

    [Fact]
    public async Task A_missing_request_or_draft_is_null_not_an_error()
    {
        var sap = new FakeServiceLayer();
        sap.On(_ => true, _ => Json("""{"error":{"message":{"value":"not found"}}}""", HttpStatusCode.NotFound));
        var client = CreateClient(sap);

        Assert.Null(await client.GetApprovalRequestAsync(404));
        Assert.Null(await client.GetCreditNoteDraftAsync(404));
    }

    /// <summary>
    /// The stream is the one read where a 404 is not "no such file". A Service Layer whose own
    /// attachments folder is unmounted answers every <c>$value</c> with
    /// <c>404 Fail to get the LINUX mount point for AttachmentsFolderPath</c> — the exact body below,
    /// taken from KEFALOS_TEST_3 on 2026-09-02. Collapsing that to null told the person the document
    /// they were looking at had no attachment, when SAP was merely refusing to hand it over.
    /// </summary>
    [Fact]
    public async Task A_refused_stream_carries_saps_reason_rather_than_reading_as_a_missing_file()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Path.Contains("/$value"),
            _ => Json("""{"error":{"code":404,"message":{"lang":"en-us","value":"Fail to get the LINUX mount point for AttachmentsFolderPath"}}}""", HttpStatusCode.NotFound));
        var client = CreateClient(sap);

        var rejection = await Assert.ThrowsAsync<SapRequestRejectedException>(
            () => client.DownloadAttachmentAsync(5, "epic 11.pdf"));

        Assert.Equal("Fail to get the LINUX mount point for AttachmentsFolderPath", rejection.SapMessage);
        Assert.Contains("epic 11.pdf", rejection.Operation);
    }

    [Fact]
    public async Task A_401_is_answered_by_one_re_login_and_one_retry()
    {
        var sap = new FakeServiceLayer();
        var calls = 0;
        sap.On(r => r.Path.EndsWith("/ApprovalRequests(5)"), _ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json("""{"Code":5,"Status":"arsPending"}"""));
        var client = CreateClient(sap);
        var loginsBefore = sap.LoginCount;

        var request = await client.GetApprovalRequestAsync(5);

        Assert.NotNull(request);
        Assert.Equal(2, sap.Requests.Count(r => r.Path.EndsWith("/ApprovalRequests(5)")));
        Assert.True(sap.LoginCount > loginsBefore, "the 401 must trigger a re-login");
    }

    [Fact]
    public async Task Drafts_are_read_in_batches_from_the_drafts_set_with_the_object_code_filter()
    {
        var sap = new FakeServiceLayer();
        sap.On(r => r.Path.EndsWith("/Drafts"),
            _ => Json("""{"value":[{"DocEntry":2,"DocObjectCode":"oCreditNotes"},{"DocEntry":1,"DocObjectCode":"oCreditNotes"}]}"""));
        var client = CreateClient(sap);

        var drafts = await client.GetCreditNoteDraftsAsync([1, 2, 2, 0]);

        Assert.Equal(2, drafts.Count);
        var get = Assert.Single(sap.Requests);
        var query = Uri.UnescapeDataString(get.Query);
        Assert.Contains("$filter=DocObjectCode eq 'oCreditNotes' and (DocEntry eq 2 or DocEntry eq 1)", query);
        Assert.Contains("AttachmentEntry,AuthorizationStatus,DocObjectCode&$orderby=DocEntry desc", query);
        Assert.DoesNotContain("DocumentLines", query);
    }

    [Fact]
    public async Task A_share_read_takes_the_file_from_the_attachments_folder_by_its_full_name()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"sap-attachments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 share");
            await File.WriteAllBytesAsync(Path.Combine(folder, "return-note.pdf"), bytes);
            var client = CreateClient(new FakeServiceLayer(), new SAPSettings { AttachmentsPath = folder });
            var line = new SAPAttachmentLine { FileName = "return-note", FileExtension = "pdf", SourcePath = @"C:\Users\clerk\Desktop" };

            using var download = await client.ReadAttachmentFromShareAsync(line);

            Assert.NotNull(download);
            Assert.Equal("application/pdf", download.ContentType);
            using var read = new MemoryStream();
            await download.Content.CopyToAsync(read);
            Assert.Equal(bytes, read.ToArray());

            Assert.Null(await client.ReadAttachmentFromShareAsync(new SAPAttachmentLine { FileName = "missing", FileExtension = "pdf" }));

            // A name SAP hands back is not this application's to trust, and the guard cannot lean on
            // the running platform's idea of a separator: a backslash is one on Windows and an
            // ordinary character on Linux, so a Windows-only check passes this on a Linux CI runner.
            foreach (var traversal in new[] { @"..\..\secrets", "../../secrets", "/etc/passwd", @"C:\Windows\System32\config\SAM" })
            {
                await Assert.ThrowsAsync<ArgumentException>(
                    () => client.ReadAttachmentFromShareAsync(
                        new SAPAttachmentLine { FileName = traversal, FileExtension = "txt" }));
            }

            // A line with no extension at all, whose whole name is the parent directory.
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.ReadAttachmentFromShareAsync(new SAPAttachmentLine { FileName = ".." }));

            // A dot inside the name is not a traversal, and must still be readable.
            await File.WriteAllBytesAsync(Path.Combine(folder, "report..final.pdf"), bytes);
            using var dotted = await client.ReadAttachmentFromShareAsync(
                new SAPAttachmentLine { FileName = "report..final", FileExtension = "pdf" });
            Assert.NotNull(dotted);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A share the server cannot open and a file SAP no longer holds both answer false from
    /// <c>File.Exists</c>, and they are fixed by different people — one is the file server or the app
    /// pool's credentials, the other is a stale attachment line. Reporting the first as "not in the
    /// folder" sends somebody hunting through a folder they cannot even reach.
    /// </summary>
    [Fact]
    public async Task An_unreachable_attachments_folder_is_not_reported_as_a_missing_file()
    {
        var missingFolder = Path.Combine(Path.GetTempPath(), $"sap-attachments-{Guid.NewGuid():N}", "never-created");
        var client = CreateClient(new FakeServiceLayer(), new SAPSettings { AttachmentsPath = missingFolder });
        var line = new SAPAttachmentLine { FileName = "return-note", FileExtension = "pdf" };

        var failure = await Assert.ThrowsAsync<IOException>(() => client.ReadAttachmentFromShareAsync(line));

        Assert.Contains("could not be reached", failure.Message, StringComparison.Ordinal);
        Assert.Contains(missingFolder, failure.Message, StringComparison.Ordinal);
    }

    private static SAPServiceLayerClient CreateClient(FakeServiceLayer sap, SAPSettings? settings = null)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();
        settings ??= new SAPSettings();
        settings.ServiceLayerUrl = "https://sap.invalid/b1s/v1/";

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(settings),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            new StubItemUomMappingStore());
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage Bytes(byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Query, string? Body, string? Prefer, string? Cookie);

    /// <summary>Answers login itself, records everything else, and routes by predicate.</summary>
    private sealed class FakeServiceLayer : HttpMessageHandler
    {
        private readonly List<(Func<RecordedRequest, bool> Match, Func<RecordedRequest, HttpResponseMessage> Respond)> _routes = [];

        public List<RecordedRequest> Requests { get; } = [];
        public int LoginCount { get; private set; }

        public void On(Func<RecordedRequest, bool> match, Func<RecordedRequest, HttpResponseMessage> respond)
            => _routes.Add((match, respond));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Login", StringComparison.Ordinal))
            {
                LoginCount++;
                return Json("""{"SessionId":"test-session"}""");
            }

            if (path.EndsWith("/Logout", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method,
                path,
                request.RequestUri.Query,
                body,
                request.Headers.TryGetValues("Prefer", out var prefer) ? string.Join(";", prefer) : null,
                request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join(";", cookie) : null);
            Requests.Add(recorded);

            foreach (var (match, respond) in _routes)
            {
                if (match(recorded))
                {
                    return respond(recorded);
                }
            }

            return Json("""{"error":{"message":{"value":"no route"}}}""", HttpStatusCode.NotFound);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubItemUomMappingStore : ISapItemUomMappingStore
    {
        public Task<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>> GetAsync(
            IReadOnlyCollection<SapItemUomKey> keys,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>>(
                new Dictionary<SapItemUomKey, (string? UoMCode, int UoMEntry)>());

        public Task SaveAsync(
            IReadOnlyCollection<(SapItemUomKey Key, string? UoMCode, int UoMEntry)> mappings,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
