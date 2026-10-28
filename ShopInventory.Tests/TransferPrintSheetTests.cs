using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace ShopInventory.Tests;

/// <summary>
/// The draft print sheets on the transfers page: held transfers and transfer requests share one
/// A4 layout. These build the real sheet HTML from the page and check what a printed copy says.
/// </summary>
public class TransferPrintSheetTests(ITestOutputHelper output)
{
    [Fact]
    public void OpenTransferRequest_PrintsAsDraftWithItsLines()
    {
        var html = Build("BuildTransferRequestDraftHtml", Request(documentStatus: "bost_Open", approvalStatus: "Pending"));
        output.WriteLine(html);

        Assert.Contains("<div class=\"watermark\">DRAFT</div>", html);
        Assert.Contains("Transfer Request &mdash; Draft", html);
        Assert.Contains("NOT POSTED TO SAP", html);
        Assert.Contains("#10453", html);
        Assert.Contains("YOG029", html);
        Assert.Contains("150g x 10 Banana Yoghurt", html);
        Assert.Contains("Lydia Musandirire", html);
        Assert.Contains("Administrator Review", html);
        Assert.Contains("No stock has moved", html);
    }

    [Fact]
    public void ClosedTransferRequest_PrintsWithoutDraftMarkings()
    {
        var html = Build("BuildTransferRequestDraftHtml", Request(documentStatus: "bost_Close", approvalStatus: "Generated"));

        Assert.DoesNotContain("class=\"watermark\"", html);
        Assert.DoesNotContain("NOT POSTED TO SAP", html);
        Assert.Contains("can no longer be converted", html);
    }

    [Fact]
    public void TransferRequestText_IsEncoded()
    {
        var request = Request(documentStatus: "bost_Open", approvalStatus: null);
        request.Comments = "<script>alert(1)</script>";

        var html = Build("BuildTransferRequestDraftHtml", request);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void HeldTransfer_StillPrintsTheSameDraftSheet()
    {
        var pending = new PendingInventoryTransferDto
        {
            Id = Guid.NewGuid(),
            DraftNumber = "DRF-00042",
            FromWarehouse = "KEFGRC",
            ToWarehouse = "VAN001",
            Status = "Pending",
            CreatedByName = "Depot Controller",
            CreatedAtUtc = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc),
            Lines = [new PendingInventoryTransferLineDto { ItemCode = "YOG029", Quantity = 5, UoMCode = "EA" }]
        };

        var html = Build("BuildPendingTransferDraftHtml", pending);

        Assert.Contains("<div class=\"watermark\">DRAFT</div>", html);
        Assert.Contains("Inventory Transfer &mdash; Draft", html);
        Assert.Contains("Submitted by", html);
        Assert.Contains("Depot Controller", html);
        Assert.Contains("YOG029", html);
    }

    private static InventoryTransferRequestDto Request(string documentStatus, string? approvalStatus) => new()
    {
        DocEntry = 20453,
        DocNum = 10453,
        DocDate = "2026-10-27",
        DueDate = "2026-10-28",
        FromWarehouse = "KEFGRC",
        ToWarehouse = "VAN001",
        DocumentStatus = documentStatus,
        ApprovalStatus = approvalStatus,
        RequesterName = "Lydia Musandirire",
        RequesterEmail = "lydia.musandirire@kefalos.com",
        Comments = "Inventory request 27/10/2026",
        ApprovalTemplateName = "General Transfer Review",
        ApprovalStages =
        [
            new ApprovalStageProgressModel
            {
                StageName = "Administrator Review",
                ApprovalsRequired = 1,
                Status = "Pending",
                AuthorizerRoles = ["Admin"]
            }
        ],
        Lines =
        [
            new InventoryTransferRequestLineDto
            {
                LineNum = 0, ItemCode = "YOG029", ItemDescription = "150g x 10 Banana Yoghurt",
                Quantity = 5, FromWarehouseCode = "KEFGRC", ToWarehouseCode = "VAN001", UoMCode = "EA"
            },
            new InventoryTransferRequestLineDto
            {
                LineNum = 1, ItemCode = "YOG030", ItemDescription = "150g x 10 Strawberry Smooth Yoghurt",
                Quantity = 5, FromWarehouseCode = "KEFGRC", ToWarehouseCode = "VAN001", UoMCode = "EA"
            }
        ]
    };

    private static string Build(string method, object document)
    {
        var page = new InventoryTransfers();
        Inject(page, "Logger", NullLogger<InventoryTransfers>.Instance);
        Inject(page, "AppSettings", new FixedSettings());
        Inject(page, "Env", new WebRoot());

        var builder = typeof(InventoryTransfers).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{method} not found on InventoryTransfers");
        return (string)builder.Invoke(page, [document])!;
    }

    private static void Inject(object page, string name, object value) =>
        (typeof(InventoryTransfers).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} not found on InventoryTransfers"))
        .SetValue(page, value);

    private sealed class FixedSettings : IAppSettingsProvider
    {
        public string CompanyName => "Kefalos";
        public string DefaultWarehouse => string.Empty;
        public string DefaultCurrency => "USD";
        public string DateFormat => "dd MMM yyyy";
        public int PageSize => 25;
        public int CacheDurationMinutes => 5;
        public bool ShowPricesWithTax => false;
        public Task ReloadAsync() => Task.CompletedTask;
    }

    /// <summary>Points the logo lookup at a folder with no logo, so the sheet prints without one.</summary>
    private sealed class WebRoot : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "ShopInventory.Web";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
