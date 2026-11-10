using System.Net;
using System.Text;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The fiscalisation console's refusal reaches the person who raised the document in the console's own words.
/// </summary>
/// <remarks>
/// On 2026-11-09 the console refused SAP credit memos 62766 and 62752 (RCPT035: the note credits more than is left
/// on its invoice) and answered with a plain sentence saying what to do. ShopInventory read only the detail, and the
/// Create credit note page said "created successfully".
/// </remarks>
public sealed class FiscalisationRefusalMessageTests
{
    private const string Detail =
        "Device 46670: RCPT035 prevention: The total amount of all credit notes (300.00) exceeds the original invoice (265.65).";

    private const string Sentence =
        "The note credits more than is left on its invoice. Reduce the note to 265.65 or less, or link it to the right "
        + "invoice, then fiscalise the document again.";

    [Fact]
    public async Task The_client_reads_the_consoles_sentence_beside_its_detail()
    {
        var client = Client(HttpStatusCode.BadRequest, $$"""
            {"type":"about:blank","title":"Bad Request","status":400,"detail":"{{Detail}}","errorCode":"RCPT035",
             "userMessage":"{{Sentence}}","traceId":"0HN7"}
            """);

        var refusal = await Assert.ThrowsAsync<FiscalisationApiException>(
            () => client.SubmitSapReceiptAsync(new SapFiscaliseReceiptApiRequest()));

        Assert.Equal("RCPT035", refusal.ErrorCode);
        Assert.Equal(Detail, refusal.Message);
        Assert.Equal(Sentence, refusal.UserMessage);
    }

    [Fact]
    public async Task A_console_that_sends_no_sentence_leaves_it_empty()
    {
        var client = Client(HttpStatusCode.BadRequest, $$"""{"detail":"{{Detail}}","errorCode":"RCPT035"}""");

        var refusal = await Assert.ThrowsAsync<FiscalisationApiException>(
            () => client.SubmitSapReceiptAsync(new SapFiscaliseReceiptApiRequest()));

        Assert.Null(refusal.UserMessage);
    }

    [Fact]
    public async Task A_refused_credit_note_keeps_the_detail_as_its_message_and_carries_the_sentence_for_the_user()
    {
        var service = Service(new FiscalisationApiException(HttpStatusCode.BadRequest, "RCPT035", Detail, userMessage: Sentence));

        var result = await service.FiscalizeCreditNoteAsync(CreditNote(), "784900");

        Assert.False(result.Success);
        Assert.Equal("RCPT035", result.ErrorCode);
        Assert.Equal(Detail, result.Message);
        Assert.Equal(Sentence, result.UserMessage);
        Assert.Equal(Sentence, result.MessageForUser());
    }

    [Fact]
    public void Without_a_sentence_the_user_is_shown_the_message()
    {
        var result = new FiscalizationResult { Success = false, Message = Detail };

        Assert.Equal(Detail, result.MessageForUser());
    }

    [Theory]
    [InlineData("RCPT035", true)]
    [InlineData("rcpt036", true)]
    [InlineData("RCPT042", true)]
    // The original may be filed later, and then the note goes through.
    [InlineData("RCPT032", false)]
    // Put right in the console's settings (a disabled tax group, say).
    [InlineData("ValidationFailed", false)]
    [InlineData("FdmsTimeout", false)]
    [InlineData(null, false)]
    public void Only_a_rule_no_retry_can_satisfy_counts_as_a_refusal_of_the_document(string? errorCode, bool expected)
        => Assert.Equal(expected, FiscalDocumentRefusal.Is(new FiscalizationResult { Success = false, ErrorCode = errorCode }));

    [Fact]
    public void An_unresolved_outcome_is_never_read_as_a_refusal_of_the_document()
        => Assert.False(FiscalDocumentRefusal.Is(
            new FiscalizationResult { Success = false, ErrorCode = "RCPT035", RequiresReconciliation = true }));

    [Fact]
    public void Marking_a_message_twice_marks_it_once()
    {
        var marked = FiscalDocumentRefusal.Mark(FiscalDocumentRefusal.Mark(Detail));

        Assert.True(FiscalDocumentRefusal.IsMarked(marked));
        Assert.Equal(FiscalDocumentRefusal.Prefix + Detail, marked);
        Assert.False(FiscalDocumentRefusal.IsMarked(Detail));
    }

    [Fact]
    public async Task A_credit_note_refused_when_it_was_created_is_recorded_as_refused_for_what_it_says()
    {
        var recorded = await SyncAsync(new CreditNoteDto
        {
            SAPDocNum = 62766,
            CreditNoteNumber = "CN-2026-0042",
            CardCode = "COR006",
            FiscalisationRefusal = Sentence,
            FiscalisationErrorCode = "RCPT035"
        });

        Assert.True(FiscalDocumentRefusal.IsMarked(recorded.Request.Message));
        Assert.Contains(Sentence, recorded.Request.Message);
    }

    [Fact]
    public async Task A_credit_note_not_fiscalised_for_another_reason_is_recorded_as_before()
    {
        var recorded = await SyncAsync(new CreditNoteDto
        {
            SAPDocNum = 62767,
            CreditNoteNumber = "CN-2026-0043",
            CardCode = "COR006",
            FiscalisationRefusal = "ZIMRA could not be reached, so nothing was sent.",
            FiscalisationErrorCode = "FdmsRequestNotSent"
        });

        Assert.Equal("Credit note 62767 is not fiscalised.", recorded.Request.Message);
    }

    // ── Harness ─────────────────────────────────────────────────────────

    private static async Task<SyncFiscalTransactionCommand> SyncAsync(CreditNoteDto creditNote)
    {
        SyncFiscalTransactionCommand? recorded = null;
        var reader = StubProxy.For<IFiscalReceiptReader>((_, _) => Task.FromResult<FiscalReceiptSnapshot?>(
            new FiscalReceiptSnapshot(false, null, null, null, null, null, null, DateTime.UtcNow, null)));
        var sender = StubProxy.For<ISender>((_, args) =>
        {
            recorded = (SyncFiscalTransactionCommand)args![0]!;
            return Task.FromResult<ErrorOr<FiscalTransactionLogItemDto>>(new FiscalTransactionLogItemDto());
        });

        await CreditNoteFiscalTransactionSync.SyncAsync(
            creditNote, reader, sender, NullLogger.Instance, userId: null, CancellationToken.None);

        return recorded ?? throw new InvalidOperationException("Nothing was recorded.");
    }

    private static FiscalizationService Service(FiscalisationApiException refusal)
    {
        var client = StubProxy.For<IFiscalisationApiClient>((method, _) =>
        {
            if (method.Name != nameof(IFiscalisationApiClient.SubmitSapReceiptAsync))
            {
                throw new InvalidOperationException($"IFiscalisationApiClient.{method.Name} was not expected.");
            }

            return Task.FromException<SubmitReceiptApiResponse>(refusal);
        });

        return new FiscalizationService(
            client,
            StubProxy.For<IFiscalDeviceConfigCache>((_, _) => Task.FromResult<FiscalConfigApiResponse?>(null)),
            Options.Create(new FiscalisationSettings { Enabled = true }),
            Options.Create(new TaxSettings()),
            NullLogger<FiscalizationService>.Instance);
    }

    private static InvoiceDto CreditNote() => new()
    {
        DocEntry = 90211,
        DocNum = 62766,
        DocDate = "2026-11-09",
        DocCurrency = "USD",
        DocTotal = 300m,
        Lines = [new InvoiceLineDto { LineNum = 0, ItemCode = "A", Quantity = 1, GrossPrice = 300m, WarehouseCode = "KEFGRS" }]
    };

    private static FiscalisationApiClient Client(HttpStatusCode status, string body)
    {
        var settings = new FiscalisationSettings
        {
            BaseUrl = "https://fiscal.example/",
            ApiKey = "installed-key",
            DefaultDeviceId = 1,
            TransientRetryCount = 0
        };

        var httpClient = new HttpClient(new FixedResponse(status, body)) { BaseAddress = new Uri(settings.BaseUrl) };
        return new FiscalisationApiClient(httpClient, Options.Create(settings), NullLogger<FiscalisationApiClient>.Instance);
    }

    private sealed class FixedResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json")
            });
    }
}
