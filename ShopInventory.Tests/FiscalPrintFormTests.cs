using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.FiscalPrintForms;
using ShopInventory.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the per-partner fiscal document type: a partner set to Receipt48 has its till, vending and van
/// sales filed as a 48 mm receipt, and everything else — other channels, other partners — stays an A4
/// invoice.
/// </summary>
public sealed class FiscalPrintFormTests : IDisposable
{
    private const string ReceiptPartner = "CASH-SHOP-01";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FiscalPrintFormTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _context.BusinessPartnerFiscalPrintForms.Add(new BusinessPartnerFiscalPrintFormEntity
        {
            CardCode = ReceiptPartner,
            PrintForm = ReceiptPrintForm.Receipt48
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private FiscalPrintFormResolver Resolver() => new(_context, NullLogger<FiscalPrintFormResolver>.Instance);

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.Vending)]
    [InlineData(SaleSourceSystems.VanSales)]
    [InlineData(SaleSourceSystems.VanSalesOnline)]
    [InlineData("kefalosshoptill")]
    public async Task A_till_vending_or_van_sale_takes_the_partners_choice(string sourceSystem)
    {
        Assert.Equal(
            ReceiptPrintForm.Receipt48,
            await Resolver().ResolveAsync(sourceSystem, $" {ReceiptPartner} ", CancellationToken.None));
    }

    [Theory]
    [InlineData(SaleSourceSystems.LegacyDesktop)]
    [InlineData("Desktop")]
    [InlineData("CreditNote")]
    [InlineData(null)]
    public async Task Every_other_channel_is_an_A4_invoice_whatever_the_partner_chose(string? sourceSystem)
    {
        Assert.Equal(
            ReceiptPrintForm.InvoiceA4,
            await Resolver().ResolveAsync(sourceSystem, ReceiptPartner, CancellationToken.None));
    }

    [Fact]
    public async Task A_partner_with_no_choice_is_an_A4_invoice()
    {
        Assert.Equal(
            ReceiptPrintForm.InvoiceA4,
            await Resolver().ResolveAsync(SaleSourceSystems.ShopTill, "SOMEONE-ELSE", CancellationToken.None));
    }

    [Fact]
    public async Task The_fiscaliser_files_a_till_sale_under_the_partners_choice()
    {
        object? sentForm = null;

        var fiscalisation = StubProxy.For<IFiscalizationService>((method, args) =>
        {
            if (method.Name != nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync))
            {
                throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.");
            }

            sentForm = args![4];
            return Task.FromResult(new FiscalizationResult { Success = true, ReceiptGlobalNo = "1" });
        });

        var fiscaliser = new DesktopSaleFiscaliser(
            fiscalisation,
            StubProxy.Unused<INotificationService>(),
            Options.Create(new TaxSettings()),
            Resolver(),
            NullLogger<DesktopSaleFiscaliser>.Instance);

        await fiscaliser.FiscaliseAsync(
            new DesktopSaleEntity
            {
                ExternalReferenceId = "KEF-SHOP-0001",
                SourceSystem = SaleSourceSystems.ShopTill,
                CardCode = ReceiptPartner,
                DocDate = new DateTime(2026, 9, 30),
                Currency = "USD",
                TotalAmount = 1m,
                PaymentMethod = TenderTypes.Cash
            },
            CancellationToken.None);

        Assert.Equal(ReceiptPrintForm.Receipt48, sentForm);
    }

    [Theory]
    [InlineData(ReceiptPrintForm.Receipt48)]
    [InlineData(ReceiptPrintForm.InvoiceA4)]
    public async Task The_platform_receives_the_form_it_was_given(ReceiptPrintForm printForm)
    {
        SubmitReceiptApiRequest? submitted = null;

        var client = StubProxy.For<IFiscalisationApiClient>((method, args) =>
        {
            if (method.Name != nameof(IFiscalisationApiClient.SubmitReceiptAsync))
            {
                throw new InvalidOperationException($"IFiscalisationApiClient.{method.Name} was not expected.");
            }

            submitted = (SubmitReceiptApiRequest)args![0]!;
            return Task.FromResult(new SubmitReceiptApiResponse { Success = true });
        });

        var configCache = StubProxy.For<IFiscalDeviceConfigCache>((_, _) =>
            Task.FromResult<FiscalConfigApiResponse?>(null));

        var service = new FiscalizationService(
            client,
            configCache,
            Options.Create(new FiscalisationSettings { Enabled = true }),
            Options.Create(new TaxSettings()),
            NullLogger<FiscalizationService>.Instance);

        await service.FiscalizePreSapInvoiceAsync(
            new InvoiceDto
            {
                DocDate = "2026-09-30",
                DocCurrency = "USD",
                DocTotal = 1m,
                Lines = [new InvoiceLineDto { LineNum = 1, ItemCode = "A", Quantity = 1, GrossPrice = 1m }]
            },
            "KEF-SHOP-0002",
            printForm: printForm);

        Assert.NotNull(submitted);
        Assert.Equal(printForm, submitted.ReceiptPrintForm);
    }

    [Theory]
    [InlineData("Receipt48", true)]
    [InlineData("invoicea4", true)]
    [InlineData("1", false)]
    [InlineData("Receipt80", false)]
    public void Only_the_two_document_names_are_accepted(string printForm, bool valid)
    {
        var result = new SaveFiscalPrintFormValidator().Validate(
            new SaveFiscalPrintFormCommand(ReceiptPartner, null, printForm, "admin"));

        Assert.Equal(valid, result.IsValid);
    }
}
