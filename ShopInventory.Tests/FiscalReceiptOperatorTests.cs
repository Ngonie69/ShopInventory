using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// An invoice raised in ShopInventory names its user to the fiscalisation platform, whose Receipts page otherwise shows
/// ShopInventory's own SAP account as the raiser of every one (798346: "Software").
/// </summary>
public sealed class FiscalReceiptOperatorTests
{
    [Fact]
    public void A_user_is_named_by_username_and_full_name()
    {
        var raisedBy = FiscalReceiptOperator.For(" tatenda ", " Tatenda ", "Moyo");

        Assert.Equal(new FiscalReceiptOperator("tatenda", "Tatenda Moyo"), raisedBy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void No_username_names_nobody(string? username)
        => Assert.Null(FiscalReceiptOperator.For(username, "Tatenda", "Moyo"));

    [Fact]
    public void A_user_without_a_name_is_named_by_username_alone()
    {
        var request = new SubmitReceiptApiRequest();

        FiscalReceiptOperator.For("cashier2")!.ApplyTo(request);

        // A full name the platform would otherwise fill with its "SAP Operator" stand-in.
        Assert.Equal("cashier2", request.Username);
        Assert.Equal("cashier2", request.UserNameSurname);
    }

    [Fact]
    public void Names_longer_than_the_platform_takes_are_cut_rather_than_refused()
    {
        var request = new SubmitReceiptApiRequest();

        new FiscalReceiptOperator(new string('u', 120), new string('n', 300)).ApplyTo(request);

        Assert.Equal(FiscalReceiptOperator.MaxUsernameLength, request.Username!.Length);
        Assert.Equal(FiscalReceiptOperator.MaxFullNameLength, request.UserNameSurname!.Length);
    }

    [Fact]
    public async Task An_invoice_raised_here_names_its_user_to_the_platform()
    {
        var submitted = await SubmitAsync(service => service.FiscalizeInvoiceAsync(
            SapInvoice(),
            raisedBy: FiscalReceiptOperator.For("tatenda", "Tatenda", "Moyo")));

        Assert.Equal("tatenda", submitted.Receipt!.Username);
        Assert.Equal("Tatenda Moyo", submitted.Receipt.UserNameSurname);
    }

    [Fact]
    public async Task An_invoice_fiscalised_without_its_raiser_names_nobody()
    {
        // The platform then names the SAP user who created the document.
        var submitted = await SubmitAsync(service => service.FiscalizeInvoiceAsync(SapInvoice()));

        Assert.Null(submitted.Receipt!.Username);
        Assert.Null(submitted.Receipt.UserNameSurname);
    }

    [Fact]
    public async Task A_credit_note_names_nobody()
    {
        var submitted = await SubmitAsync(service => service.FiscalizeCreditNoteAsync(SapInvoice(), "784900"));

        Assert.Null(submitted.Receipt!.Username);
    }

    private static InvoiceDto SapInvoice() => new()
    {
        DocEntry = 501,
        DocNum = 798346,
        DocDate = "2026-10-24",
        DocCurrency = "USD",
        DocTotal = 1m,
        Lines = [new InvoiceLineDto { LineNum = 1, ItemCode = "A", Quantity = 1, GrossPrice = 1m, WarehouseCode = "KEFGRS" }]
    };

    private static async Task<SapFiscaliseReceiptApiRequest> SubmitAsync(Func<FiscalizationService, Task> act)
    {
        SapFiscaliseReceiptApiRequest? captured = null;
        var client = StubProxy.For<IFiscalisationApiClient>((method, args) =>
        {
            if (method.Name != nameof(IFiscalisationApiClient.SubmitSapReceiptAsync))
            {
                throw new InvalidOperationException($"IFiscalisationApiClient.{method.Name} was not expected.");
            }

            captured = (SapFiscaliseReceiptApiRequest)args![0]!;
            return Task.FromResult(new SubmitReceiptApiResponse { Success = true });
        });

        var service = new FiscalizationService(
            client,
            StubProxy.For<IFiscalDeviceConfigCache>((_, _) => Task.FromResult<FiscalConfigApiResponse?>(null)),
            Options.Create(new FiscalisationSettings { Enabled = true }),
            Options.Create(new TaxSettings()),
            NullLogger<FiscalizationService>.Instance);

        await act(service);
        return captured ?? throw new InvalidOperationException("SubmitSapReceiptAsync was never called.");
    }
}
