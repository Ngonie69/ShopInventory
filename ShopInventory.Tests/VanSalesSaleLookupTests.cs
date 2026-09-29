using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Maintenance;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSaleByVanOrder;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// <c>GET api/vansales/sale/{vanOrder}</c> — a handset asking whether the sale its lost reply was about
/// landed.
/// </summary>
/// <remarks>
/// <para>The handset binds this by name and was written against the contract at the same time as the
/// route, so the JSON is asserted as text rather than only as properties: a renamed property is a field
/// the handset silently reads as null.</para>
///
/// <para>Two answers matter more than the rest. A sale that is not there must be a 200 with
/// <c>found: false</c> — the handset reads anything else as "could not check" and leaves the rep guessing
/// whether to sell the goods again. And another van's sale must look exactly like that too.</para>
/// </remarks>
public sealed class VanSalesSaleLookupTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid SecondRep = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid OtherVanRep = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private const string VanBusinessPartner = "C-VAN-014";
    private const string OtherVanBusinessPartner = "C-VAN-022";

    private const string VanOrder = "VAN014-INV-20260929-AB12CD";

    /// <summary>The API's own serializer settings: web defaults, nulls written.</summary>
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesSaleLookupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SnapshotSqliteContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Found ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_signed_and_posted_sale_answers_with_its_receipt_and_invoice()
    {
        GivenRep(Rep, VanBusinessPartner);
        var saleId = GivenSale(VanOrder, createdBy: Rep, sapDocNum: 4567, sapDocEntry: 8910);

        var answer = await Lookup(Rep, VanOrder);

        Assert.True(answer.Found);
        Assert.True(answer.Fiscalised);
        Assert.Equal($"INV{saleId}", answer.SaleNumber);
        Assert.Equal(DesktopSaleNumber.Format(saleId), answer.SaleNumber);
        Assert.Equal(4567, answer.SapDocNum);
        Assert.Equal(8910, answer.SapDocEntry);

        Assert.Equal(
            "{" +
            "\"found\":true," +
            $"\"van_order\":\"{VanOrder}\"," +
            $"\"sale_number\":\"INV{saleId}\"," +
            "\"fiscalised\":true," +
            "\"fiscal_status\":\"Success\"," +
            "\"verification_code\":\"07E3-19D4-D197-3BB5\"," +
            "\"qr_code\":\"https://fdms.zimra.co.zw/verify?y\"," +
            "\"fiscal_day\":\"538\"," +
            "\"device_serial\":\"8DE6996C0188\"," +
            "\"receipt_global_no\":221595," +
            "\"sap_doc_num\":4567," +
            "\"sap_doc_entry\":8910," +
            $"\"card_code\":\"{VanBusinessPartner}\"," +
            "\"card_name\":\"Mai Tendai Tuckshop\"," +
            "\"total\":23.68," +
            "\"currency\":\"USD\"," +
            "\"created_at_utc\":\"2026-09-29T08:12:00Z\"" +
            "}",
            JsonSerializer.Serialize(answer, ApiJson));
    }

    /// <summary>
    /// The case the route exists for. Signed at the counter, handed to the queue, not in SAP yet — so
    /// invoice history, which reads SAP, has nothing, and the receipt is still here to reprint.
    /// </summary>
    [Fact]
    public async Task A_sale_signed_but_not_yet_posted_is_found_with_no_sap_numbers()
    {
        GivenRep(Rep, VanBusinessPartner);
        var saleId = GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null);

        var answer = await Lookup(Rep, VanOrder);

        Assert.True(answer.Found);
        Assert.True(answer.Fiscalised);
        Assert.Equal($"INV{saleId}", answer.SaleNumber);
        Assert.Equal("07E3-19D4-D197-3BB5", answer.VerificationCode);
        Assert.Null(answer.SapDocNum);
        Assert.Null(answer.SapDocEntry);

        var json = JsonSerializer.Serialize(answer, ApiJson);
        Assert.Contains("\"sap_doc_num\":null", json);
        Assert.Contains("\"sap_doc_entry\":null", json);
    }

    /// <summary>
    /// The office signed the receipt, so the number is only the device's text — never the handset's
    /// column. It is still a number to the handset.
    /// </summary>
    [Fact]
    public async Task A_receipt_the_office_signed_reports_its_number_from_the_device_text()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null, receiptGlobalNo: null, fiscalReceiptNumber: "1204");

        var answer = await Lookup(Rep, VanOrder);

        Assert.Equal(1204, answer.ReceiptGlobalNo);
    }

    [Theory]
    [InlineData(DesktopSaleFiscalizationStatus.Failed, "Failed")]
    [InlineData(DesktopSaleFiscalizationStatus.Pending, "Pending")]
    [InlineData(DesktopSaleFiscalizationStatus.Skipped, "Skipped")]
    public async Task A_sale_the_device_did_not_sign_is_found_but_not_fiscalised(
        DesktopSaleFiscalizationStatus status,
        string statusName)
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null, status: status);

        var answer = await Lookup(Rep, VanOrder);

        Assert.True(answer.Found);
        Assert.False(answer.Fiscalised);
        Assert.Equal(statusName, answer.FiscalStatus);
        Assert.Null(answer.VerificationCode);
        Assert.Null(answer.SapDocNum);
    }

    /// <summary>
    /// A success with nothing to print is not a receipt the rep can hand over, so it is not reported as
    /// one.
    /// </summary>
    [Fact]
    public async Task A_success_with_no_verification_code_is_not_fiscalised()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null, verificationCode: "  ");

        var answer = await Lookup(Rep, VanOrder);

        Assert.True(answer.Found);
        Assert.False(answer.Fiscalised);
        Assert.Equal("Success", answer.FiscalStatus);
    }

    // ── Not found ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_van_order_with_no_sale_is_found_false_with_everything_else_empty()
    {
        GivenRep(Rep, VanBusinessPartner);

        var answer = await Lookup(Rep, "VAN014-INV-20260929-NOSUCH");

        Assert.Equal(
            "{" +
            "\"found\":false," +
            "\"van_order\":\"VAN014-INV-20260929-NOSUCH\"," +
            "\"sale_number\":null," +
            "\"fiscalised\":false," +
            "\"fiscal_status\":null," +
            "\"verification_code\":null," +
            "\"qr_code\":null," +
            "\"fiscal_day\":null," +
            "\"device_serial\":null," +
            "\"receipt_global_no\":null," +
            "\"sap_doc_num\":null," +
            "\"sap_doc_entry\":null," +
            "\"card_code\":null," +
            "\"card_name\":null," +
            "\"total\":null," +
            "\"currency\":null," +
            "\"created_at_utc\":null" +
            "}",
            JsonSerializer.Serialize(answer, ApiJson));
    }

    [Fact]
    public async Task The_van_order_is_matched_as_the_post_wrote_it_trimmed()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null);

        var answer = await Lookup(Rep, $"  {VanOrder} ");

        Assert.True(answer.Found);
        Assert.Equal(VanOrder, answer.VanOrder);
    }

    [Fact]
    public async Task A_van_order_longer_than_the_column_is_not_found_rather_than_refused()
    {
        GivenRep(Rep, VanBusinessPartner);

        var answer = await Lookup(Rep, new string('X', 101));

        Assert.False(answer.Found);
    }

    // ── Scope ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Another van's sale answers exactly as a sale that does not exist. "Found, but not yours" would
    /// confirm the reference to whoever guessed it.
    /// </summary>
    [Fact]
    public async Task Another_vans_sale_answers_as_not_found()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenRep(OtherVanRep, OtherVanBusinessPartner);
        GivenSale(VanOrder, createdBy: OtherVanRep, sapDocNum: 4567, sapDocEntry: 8910, cardCode: OtherVanBusinessPartner);

        var answer = await Lookup(Rep, VanOrder);

        Assert.False(answer.Found);
        Assert.Equal(VanOrder, answer.VanOrder);
        Assert.Null(answer.SaleNumber);
        Assert.Null(answer.VerificationCode);
        Assert.Null(answer.CardCode);
        Assert.Null(answer.Total);
    }

    /// <summary>
    /// Two reps on one van is an ordinary day. The second can check a sale the first made, because the
    /// van's account is in both their scopes — the same scope invoice history is narrowed by.
    /// </summary>
    [Fact]
    public async Task A_second_rep_on_the_same_van_sees_the_sale()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenRep(SecondRep, VanBusinessPartner);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null);

        var answer = await Lookup(SecondRep, VanOrder);

        Assert.True(answer.Found);
        Assert.True(answer.Fiscalised);
    }

    /// <summary>
    /// The rep who made the sale can always ask about it, even once the account has been moved to
    /// another van — the handset asking is the one holding the unanswered post.
    /// </summary>
    [Fact]
    public async Task The_rep_who_made_the_sale_sees_it_after_moving_van()
    {
        GivenRep(Rep, OtherVanBusinessPartner);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null);

        var answer = await Lookup(Rep, VanOrder);

        Assert.True(answer.Found);
    }

    /// <summary>
    /// Only a van's own sources. A till sale is never posted under a van order, and this route is not a
    /// way to learn that one exists, even against an account in scope.
    /// </summary>
    [Fact]
    public async Task A_sale_that_is_not_a_van_sale_is_not_found()
    {
        GivenRep(Rep, VanBusinessPartner);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: 4567, sapDocEntry: 8910, source: SaleSourceSystems.ShopTill);

        var answer = await Lookup(Rep, VanOrder);

        Assert.False(answer.Found);
    }

    [Fact]
    public async Task An_inactive_account_is_refused()
    {
        GivenRep(Rep, VanBusinessPartner, isActive: false);
        GivenSale(VanOrder, createdBy: Rep, sapDocNum: null, sapDocEntry: null);

        var result = await Handler().Handle(new GetVanSalesSaleByVanOrderQuery(Rep, VanOrder), default);

        Assert.True(result.IsError);
    }

    // ── Reachable during a transactions lockout ─────────────────────────────

    /// <summary>
    /// The check is a read, and a rep who cannot sell during maintenance still needs to know whether the
    /// sale before it landed.
    /// </summary>
    [Fact]
    public void The_route_is_a_read_to_the_maintenance_gate()
    {
        Assert.True(MaintenanceGate.IsRead("GET", $"/api/vansales/sale/{VanOrder}"));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private GetVanSalesSaleByVanOrderHandler Handler() => new(
        _context, NullLogger<GetVanSalesSaleByVanOrderHandler>.Instance);

    private async Task<VanSalesSaleLookupResponse> Lookup(Guid userId, string vanOrder)
    {
        var result = await Handler().Handle(new GetVanSalesSaleByVanOrderQuery(userId, vanOrder), default);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private void GivenRep(Guid id, string businessPartner, bool isActive = true)
    {
        _context.Users.Add(new User
        {
            Id = id,
            Username = $"van-rep-{id:N}",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Sales,
            IsActive = isActive,
            AssignedBusinessPartnerCode = businessPartner
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    /// <summary>A sale row as the online post writes it — by default signed, and tax included in its total.</summary>
    private int GivenSale(
        string reference,
        Guid createdBy,
        int? sapDocNum,
        int? sapDocEntry,
        DesktopSaleFiscalizationStatus status = DesktopSaleFiscalizationStatus.Success,
        string cardCode = VanBusinessPartner,
        string source = SaleSourceSystems.VanSalesOnline,
        string? verificationCode = "07E3-19D4-D197-3BB5",
        int? receiptGlobalNo = 221595,
        string? fiscalReceiptNumber = "221595")
    {
        var signed = status == DesktopSaleFiscalizationStatus.Success;

        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = cardCode,
            CardName = "Mai Tendai Tuckshop",
            WarehouseCode = "VAN14",
            DocDate = new DateTime(2026, 9, 29),
            Currency = "USD",
            TotalAmount = 23.68m,
            VatAmount = 3.18m,
            AmountPaid = 23.68m,
            FiscalizationStatus = status,
            FiscalReceiptNumber = signed ? fiscalReceiptNumber : null,
            ReceiptGlobalNo = signed ? receiptGlobalNo : null,
            FiscalVerificationCode = signed ? verificationCode : null,
            FiscalQRCode = signed ? "https://fdms.zimra.co.zw/verify?y" : null,
            FiscalDayNo = signed ? "538" : null,
            FiscalDeviceNumber = signed ? "8DE6996C0188" : null,
            SapDocNum = sapDocNum,
            SapDocEntry = sapDocEntry,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            CreatedBy = createdBy.ToString(),
            CreatedAt = new DateTime(2026, 9, 29, 8, 12, 0, DateTimeKind.Utc)
        };

        _context.DesktopSales.Add(sale);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        return sale.Id;
    }
}
