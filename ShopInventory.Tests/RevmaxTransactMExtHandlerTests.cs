using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.Revmax.Commands.TransactMExt;
using ShopInventory.Features.Revmax.Queries.GetRevmaxInvoice;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The temporary <c>/api/revmax</c> routes. TransactMExt files with ZIMRA, so what matters is when it
/// does NOT send: every refusal below must leave <see cref="FakeRevmax.Posts"/> at zero.
/// </summary>
public sealed class RevmaxTransactMExtHandlerTests
{
    private const int OwnDevice = 22862;

    private static TransactMExtRequest Request(int refDevice = 12345) => new()
    {
        InvoiceNumber = " CN-1 ", Currency = "USD", Istatus = "02", InvoiceAmount = 10m,
        ItemsXml = new List<RevmaxRequestItem>(), CurrenciesXml = new List<RevmaxRequestCurrency>(),
        refDeviceId = refDevice, refReceiptGlobalNo = 678, refFiscalDayNo = 90
    };

    private static RevmaxTransactMExtHandler Handler(FakeRevmax device, bool enabled = true) =>
        new(device, Options.Create(new RevmaxSettings { Enabled = enabled, DefaultRefDeviceId = OwnDevice }),
            NullLogger<RevmaxTransactMExtHandler>.Instance);

    private static Task<ErrorOr<TransactMExtResponse>> Send(FakeRevmax device, TransactMExtRequest? request = null, bool enabled = true) =>
        Handler(device, enabled).Handle(new RevmaxTransactMExtCommand(request ?? Request(), "admin"), CancellationToken.None);

    private static InvoiceResponse NotFound => new() { Code = "0", Message = "Invoice not Found" };

    [Fact]
    public async Task Free_number_is_sent_once_trimmed_and_the_filed_answer_returned()
    {
        var device = new FakeRevmax { Lookup = NotFound, Answer = new TransactMExtResponse { Code = "1", ReceiptGlobalNo = "999" } };

        var result = await Send(device);

        Assert.False(result.IsError);
        Assert.Equal("999", result.Value.ReceiptGlobalNo);
        Assert.Equal(1, device.Posts);
        Assert.Equal("CN-1", device.LookedUp);
        Assert.Equal("CN-1", device.Sent!.InvoiceNumber);
    }

    [Fact]
    public async Task Number_the_device_already_holds_is_refused_unsent()
    {
        var device = new FakeRevmax
        {
            Lookup = new InvoiceResponse { Code = "1", Data = new InvoiceData { ReceiptGlobalNo = 555, ReceiptType = "CreditNote" } }
        };

        var result = await Send(device);

        Assert.Equal("Revmax.AlreadyFiled", result.FirstError.Code);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
        Assert.Equal(0, device.Posts);
    }

    [Fact]
    public async Task Busy_lookup_is_not_read_as_free()
    {
        var device = new FakeRevmax { Lookup = new InvoiceResponse { Code = "0", Message = "Device busy" } };

        var result = await Send(device);

        Assert.Equal("Revmax.DeviceUnavailable", result.FirstError.Code);
        Assert.Equal(0, device.Posts);
    }

    [Fact]
    public async Task Unreachable_lookup_sends_nothing()
    {
        var device = new FakeRevmax { LookupThrows = new HttpRequestException("timeout") };

        var result = await Send(device);

        Assert.Equal("Revmax.DeviceUnavailable", result.FirstError.Code);
        Assert.Equal(0, device.Posts);
    }

    [Fact]
    public async Task Reference_to_revmax_own_device_is_refused_unsent()
    {
        var device = new FakeRevmax { Lookup = NotFound };

        var result = await Send(device, Request(refDevice: OwnDevice));

        Assert.Equal("Revmax.OwnDeviceReference", result.FirstError.Code);
        Assert.Equal(0, device.Posts);
        Assert.Null(device.LookedUp);
    }

    [Fact]
    public async Task Disabled_revmax_sends_nothing()
    {
        var device = new FakeRevmax { Lookup = NotFound };

        var result = await Send(device, enabled: false);

        Assert.Equal("Revmax.Disabled", result.FirstError.Code);
        Assert.Equal(0, device.Posts);
    }

    [Fact]
    public async Task Device_refusal_with_http_200_is_an_error_not_a_success()
    {
        var device = new FakeRevmax { Lookup = NotFound, Answer = new TransactMExtResponse { Code = "0", Message = "Tax mismatch" } };

        var result = await Send(device);

        Assert.Equal("Revmax.Refused", result.FirstError.Code);
        Assert.Contains("Tax mismatch", result.FirstError.Description);
        Assert.Equal(1, device.Posts);
    }

    [Fact]
    public async Task Failed_post_is_reported_as_unknown_and_not_retried()
    {
        var device = new FakeRevmax { Lookup = NotFound, PostThrows = new TaskCanceledException("timed out") };

        var result = await Send(device);

        Assert.Equal("Revmax.OutcomeUnknown", result.FirstError.Code);
        Assert.Contains("/api/revmax/invoices/CN-1", result.FirstError.Description);
        Assert.Equal(1, device.Posts);
    }

    [Fact]
    public void Validator_requires_the_credit_reference()
    {
        var request = Request();
        request.refReceiptGlobalNo = null;

        var result = new RevmaxTransactMExtValidator().Validate(new RevmaxTransactMExtCommand(request, null));

        Assert.Contains(result.Errors, e => e.PropertyName.EndsWith("refReceiptGlobalNo"));
    }

    [Fact]
    public async Task GetInvoice_maps_found_not_found_and_busy()
    {
        static Task<ErrorOr<InvoiceResponse>> Get(InvoiceResponse answer) =>
            new GetRevmaxInvoiceHandler(new FakeRevmax { Lookup = answer },
                    Options.Create(new RevmaxSettings()), NullLogger<GetRevmaxInvoiceHandler>.Instance)
                .Handle(new GetRevmaxInvoiceQuery("123"), CancellationToken.None);

        Assert.False((await Get(new InvoiceResponse { Code = "1", DeviceID = "22862" })).IsError);
        Assert.Equal(ErrorType.NotFound, (await Get(NotFound)).FirstError.Type);
        Assert.Equal("Revmax.DeviceUnavailable", (await Get(new InvoiceResponse { Code = "0", Message = "busy" })).FirstError.Code);
    }

    private sealed class FakeRevmax : IRevmaxClient
    {
        public InvoiceResponse? Lookup { get; init; }
        public Exception? LookupThrows { get; init; }
        public TransactMExtResponse? Answer { get; init; }
        public Exception? PostThrows { get; init; }
        public string? LookedUp { get; private set; }
        public TransactMExtRequest? Sent { get; private set; }
        public int Posts { get; private set; }

        public Task<InvoiceResponse?> GetInvoiceAsync(string invoiceNumber, CancellationToken cancellationToken = default)
        {
            LookedUp = invoiceNumber;
            return LookupThrows is null ? Task.FromResult(Lookup) : Task.FromException<InvoiceResponse?>(LookupThrows);
        }

        public Task<TransactMExtResponse?> TransactMExtAsync(TransactMExtRequest request, CancellationToken cancellationToken = default)
        {
            Posts++;
            Sent = request;
            return PostThrows is null ? Task.FromResult(Answer) : Task.FromException<TransactMExtResponse?>(PostThrows);
        }

        public Task<CardDetailsResponse?> GetCardDetailsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DayStatusResponse?> GetDayStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LicenseResponse?> GetLicenseAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LicenseResponse?> SetLicenseAsync(string license, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZReportResponse?> GetZReportAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UnprocessedInvoicesSummaryResponse?> GetUnprocessedInvoicesSummaryAsync(string? fiscalDayNumber = null, string? fiscalDate = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransactMResponse?> TransactMAsync(TransactMRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
