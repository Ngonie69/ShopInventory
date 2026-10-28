using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using ShopInventory.Controllers;
using ShopInventory.Migrations;
using ShopInventory.Models;
using static ShopInventory.Tests.WebPageGatePermissionAlignmentTests;

namespace ShopInventory.Tests;

/// <summary>
/// A manager reads the business and does nothing to it (2026-10-28), and leaves set-up and
/// administration of the system to the administrator (2026-10-05).
/// </summary>
/// <remarks>
/// Pinned three ways, because each one alone has let a role reach what it should not: the role's API
/// defaults, the compiled Web pages' [Authorize] gates, and the API's own role gates. The migration that
/// strips the dropped codes off existing manager accounts is pinned against the defaults too, since a
/// code left on the account is a code the manager still holds.
/// </remarks>
public sealed class ManagerRoleScopeTests
{
    /// <summary>Codes that administer the system or raise documents, which a manager no longer holds.</summary>
    public static TheoryData<string> WithheldFromManager() =>
    [
        Permission.ViewUsers, Permission.CreateUsers, Permission.EditUsers, Permission.DeleteUsers,
        Permission.ManageUserRoles, Permission.ManageUserPermissions,
        Permission.ViewSettings, Permission.EditSettings, Permission.ManageSettings, Permission.ManageIntegrations,
        Permission.ViewAuditLogs, Permission.ExportAuditLogs,
        Permission.ViewSyncStatus, Permission.ManageSync, Permission.SystemAdmin,
        Permission.ManageVanSalesRoutes,
        Permission.CreateInvoices, Permission.EditInvoices, Permission.VoidInvoices,
        Permission.CreatePayments, Permission.RefundPayments, Permission.ProcessRefunds,
        Permission.CreateSalesOrders, Permission.EditSalesOrders, Permission.DeleteSalesOrders, Permission.PostSalesOrdersToSAP,
        Permission.CreateQuotations, Permission.EditQuotations,
        // Sending a customer an invoice, and pointing a customer's invoices at a phone, are the
        // cashier's; a manager reads the delivery history and sends nothing.
        Permission.SendInvoicesWhatsApp, Permission.ManageCustomerWhatsApp,
        // 2026-10-28: read-only. Nothing raised, posted, converted, approved or rejected.
        .. DroppedOn20261028,
    ];

    /// <summary>The write codes taken off the role on 2026-10-28, when it became read-only.</summary>
    private static readonly string[] DroppedOn20261028 =
    [
        Permission.CreateProducts, Permission.EditProducts, Permission.ManageProductPrices,
        Permission.TransferStock, Permission.TransferInventory, Permission.AdjustStock, Permission.AdjustInventory,
        Permission.PostStockWriteOffs,
        Permission.CreatePurchaseOrders, Permission.EditPurchaseOrders, Permission.ApprovePurchaseOrders,
        Permission.ReceivePurchaseOrders, Permission.CreatePurchaseRequests, Permission.CreatePurchaseQuotations,
        Permission.CreateGoodsReceiptPurchaseOrders, Permission.CreatePurchaseInvoices,
        Permission.ApproveSalesOrders, Permission.ApproveSapCreditNotes, Permission.AddApprovedCreditNotes,
        Permission.CreateCustomers, Permission.EditCustomers,
        Permission.FulfilVanSalesCustomerOrders, Permission.ConfirmMarketBreakages,
    ];

    [Theory]
    [MemberData(nameof(WithheldFromManager))]
    public void A_manager_holds_no_administration_or_document_raising_permission(string code)
    {
        Assert.DoesNotContain(code, Permission.GetDefaultPermissionsForRole(ApplicationRoles.Manager));
    }

    /// <summary>The business a manager reads, which making the role read-only must not take away.</summary>
    [Theory]
    [InlineData(Permission.ViewInvoices)]
    [InlineData(Permission.ViewPayments)]
    [InlineData(Permission.ViewSalesOrders)]
    [InlineData(Permission.ViewQuotations)]
    [InlineData(Permission.ViewPurchaseOrders)]
    [InlineData(Permission.ViewPurchaseRequests)]
    [InlineData(Permission.ViewPurchaseQuotations)]
    [InlineData(Permission.ViewGoodsReceiptPurchaseOrders)]
    [InlineData(Permission.ViewPurchaseInvoices)]
    [InlineData(Permission.ViewReports)]
    [InlineData(Permission.ExportReports)]
    [InlineData(Permission.ViewStock)]
    [InlineData(Permission.ViewInventory)]
    [InlineData(Permission.ViewStockWriteOffs)]
    [InlineData(Permission.ViewCustomers)]
    [InlineData(Permission.ViewProducts)]
    [InlineData(Permission.ViewVanSalesAttendance)]
    public void A_manager_keeps_the_business(string code)
    {
        Assert.Contains(code, Permission.GetDefaultPermissionsForRole(ApplicationRoles.Manager));
    }

    /// <summary>
    /// Read-only, stated once rather than code by code: every permission the role carries reads
    /// something. A code added to the role later that does anything else fails here.
    /// </summary>
    [Fact]
    public void Every_manager_permission_is_a_read()
    {
        Assert.All(
            Permission.GetDefaultPermissionsForRole(ApplicationRoles.Manager),
            code => Assert.True(code.EndsWith(".view", StringComparison.Ordinal) || code == Permission.ExportReports,
                $"{code} is not a read, and a manager holds only reads."));
    }

    /// <summary>
    /// The migration removes from existing manager accounts exactly what the role no longer carries. A
    /// code in its list that the role still held would be taken off every manager for nothing; a code
    /// withheld above but missing from the list would stay on every manager created before the change.
    /// </summary>
    [Fact]
    public void The_migration_strips_the_codes_the_role_dropped()
    {
        var sql = string.Concat(new RemoveManagerSetupPermissions().UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()
            .Select(operation => operation.Sql));
        var list = Regex.Match(sql, @"ARRAY\[(?<codes>[^\]]*)\]").Groups["codes"].Value;
        var stripped = Regex.Matches(list, "'([^']+)'").Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(stripped);

        var defaults = Permission.GetDefaultPermissionsForRole(ApplicationRoles.Manager);
        Assert.All(stripped, code => Assert.DoesNotContain(code, defaults));
        Assert.All(stripped, code => Assert.Contains(code, Permission.GetAllPermissions()));

        string[] droppedOn20261005 =
        [
            Permission.ViewUsers, Permission.ViewSettings, Permission.EditSettings, Permission.ViewAuditLogs,
            Permission.ViewSyncStatus, Permission.ManageVanSalesRoutes,
            Permission.CreateInvoices, Permission.EditInvoices, Permission.VoidInvoices,
            Permission.CreatePayments, Permission.RefundPayments, Permission.ProcessRefunds,
            Permission.CreateSalesOrders, Permission.EditSalesOrders, Permission.DeleteSalesOrders, Permission.PostSalesOrdersToSAP,
            Permission.CreateQuotations, Permission.EditQuotations,
        ];
        Assert.Equal(droppedOn20261005.Order(), stripped.Order());
    }

    /// <summary>
    /// The 2026-10-28 migration removes from existing manager accounts exactly the write codes the
    /// role dropped that day, for the same reason as the one above.
    /// </summary>
    [Fact]
    public void The_read_only_migration_strips_the_write_codes_the_role_dropped()
    {
        var sql = string.Concat(new RemoveManagerWritePermissions().UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()
            .Select(operation => operation.Sql));
        var list = Regex.Match(sql, @"ARRAY\[(?<codes>[^\]]*)\]").Groups["codes"].Value;
        var stripped = Regex.Matches(list, "'([^']+)'").Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(stripped);

        var defaults = Permission.GetDefaultPermissionsForRole(ApplicationRoles.Manager);
        Assert.All(stripped, code => Assert.DoesNotContain(code, defaults));
        Assert.All(stripped, code => Assert.Contains(code, Permission.GetAllPermissions()));
        Assert.Equal(DroppedOn20261028.Order(), stripped.Order());
    }

    /// <summary>Pages that set the system up or administer it.</summary>
    [Theory]
    [InlineData("SyncStatus")]
    [InlineData("DocumentTemplates")]
    [InlineData("ExchangeRates")]
    [InlineData("TransferListener")]
    [InlineData("UserActivity")]
    [InlineData("VanSalesRoutes")]
    [InlineData("VanSalesFleet")]
    [InlineData("RouteAssignments")]
    [InlineData("RouteCustomers")]
    [InlineData("VanSalesCustomerAccounts")]
    public void No_set_up_or_administration_page_admits_a_manager(string page)
    {
        Assert.DoesNotContain(ApplicationRoles.Manager, GateRoles(page));
    }

    /// <summary>Business pages a manager reads.</summary>
    [Theory]
    [InlineData("Invoices")]
    [InlineData("CreditNotes")]
    [InlineData("Payments")]
    [InlineData("SalesOrders")]
    [InlineData("Quotations")]
    [InlineData("ProofOfDelivery")]
    [InlineData("PodUploadReport")]
    [InlineData("PodDashboard")]
    [InlineData("StockDashboard")]
    [InlineData("ManagerDashboard")]
    [InlineData("InventoryTransfers")]
    [InlineData("PurchaseOrders")]
    [InlineData("PurchaseRequests")]
    [InlineData("PurchaseQuotations")]
    [InlineData("GoodsReceiptPurchaseOrders")]
    [InlineData("PurchaseInvoices")]
    [InlineData("StockWriteOffs")]
    [InlineData("VanSalesCustomerOrders")]
    [InlineData("ExceptionCenter")]
    [InlineData("Vending")]
    [InlineData("VanReplenishment")]
    public void A_manager_can_open_the_business_pages(string page)
    {
        Assert.Contains(ApplicationRoles.Manager, GateRoles(page));
    }

    /// <summary>
    /// The pages that raise documents stay shut to a manager, who reads the lists without writing to them.
    /// </summary>
    [Theory]
    [InlineData("CreateInvoice")]
    [InlineData("CreateCreditNote")]
    [InlineData("CreateIncomingPayment")]
    [InlineData("CreateSalesOrder")]
    [InlineData("SalesOrderEdit")]
    [InlineData("CreateQuotation")]
    [InlineData("CreateInventoryTransfer")]
    [InlineData("CreateTransferRequest")]
    [InlineData("CreatePurchaseOrder")]
    [InlineData("CreatePurchaseRequest")]
    [InlineData("CreatePurchaseQuotation")]
    [InlineData("CreateGoodsReceiptPurchaseOrder")]
    [InlineData("CreatePurchaseInvoice")]
    // Queues whose only purpose is deciding: approving credit memos, confirming breakages.
    [InlineData("CreditNoteApprovals")]
    [InlineData("MarketBreakages")]
    public void No_page_that_raises_a_document_admits_a_manager(string page)
    {
        Assert.DoesNotContain(ApplicationRoles.Manager, GateRoles(page));
    }

    /// <summary>What the opened pages read, and the manager is let through.</summary>
    [Fact]
    public async Task A_manager_passes_the_reads_behind_the_opened_pages()
    {
        Assert.True(await Passes<SalesOrderController>(nameof(SalesOrderController.GetAll), ApplicationRoles.Manager));
        Assert.True(await Passes<QuotationController>(nameof(QuotationController.GetAll), ApplicationRoles.Manager));
        Assert.True(await Passes<CreditNoteController>(nameof(CreditNoteController.GetAll), ApplicationRoles.Manager));
    }

    /// <summary>The negative control: reading the lists is not a way into writing them.</summary>
    [Fact]
    public async Task A_manager_is_refused_the_writes_on_the_opened_pages()
    {
        Assert.False(await Passes<SalesOrderController>(nameof(SalesOrderController.Create), ApplicationRoles.Manager));
        Assert.False(await Passes<SalesOrderController>(nameof(SalesOrderController.PostToSAP), ApplicationRoles.Manager));
        Assert.False(await Passes<QuotationController>(nameof(QuotationController.Create), ApplicationRoles.Manager));
        Assert.False(await Passes<CreditNoteController>(nameof(CreditNoteController.CreateFromInvoice), ApplicationRoles.Manager));
        Assert.False(await Passes<InvoiceController>(nameof(InvoiceController.CancelInvoice), ApplicationRoles.Manager));
    }

    /// <summary>The proof of delivery pages read these, and a manager reads those pages.</summary>
    [Theory]
    [InlineData(nameof(InvoiceController.GetAllPods))]
    [InlineData(nameof(InvoiceController.GetPodUploadStatus))]
    [InlineData(nameof(InvoiceController.GetPodDashboard))]
    [InlineData(nameof(InvoiceController.GetInvoiceAttachments))]
    [InlineData(nameof(InvoiceController.DownloadInvoiceAttachment))]
    public void The_pod_reads_admit_a_manager(string action)
    {
        Assert.Contains(ApplicationRoles.Manager, ActionRoles(typeof(InvoiceController), action));
    }

    /// <summary>Role-gated API set-up a manager no longer reaches.</summary>
    [Theory]
    [InlineData(typeof(DesktopIntegrationController), nameof(DesktopIntegrationController.GetTransferListenerStatus))]
    [InlineData(typeof(DesktopIntegrationController), nameof(DesktopIntegrationController.TriggerTransferListenerCheck))]
    [InlineData(typeof(DocumentController), nameof(DocumentController.CreateTemplate))]
    [InlineData(typeof(DocumentController), nameof(DocumentController.UpdateTemplate))]
    [InlineData(typeof(DocumentController), nameof(DocumentController.SetDefaultTemplate))]
    [InlineData(typeof(DocumentController), nameof(DocumentController.CreateEmailTemplate))]
    [InlineData(typeof(DocumentController), nameof(DocumentController.UpdateEmailTemplate))]
    [InlineData(typeof(VanSalesCustomerAccountsController), nameof(VanSalesCustomerAccountsController.GetAccounts))]
    [InlineData(typeof(VanSalesCustomerAccountsController), nameof(VanSalesCustomerAccountsController.Onboard))]
    [InlineData(typeof(VanSalesCustomerAccountsController), nameof(VanSalesCustomerAccountsController.Deactivate))]
    public void The_api_set_up_routes_refuse_a_manager(Type controller, string action)
    {
        var roles = ActionRoles(controller, action);
        Assert.NotEmpty(roles);
        Assert.DoesNotContain(ApplicationRoles.Manager, roles);
    }

    /// <summary>
    /// The writes a manager may still make, each with why it raises nothing. Everything else that
    /// writes refuses a manager at its role gate, its policy or its permission filter.
    /// </summary>
    private static readonly Dictionary<string, string> WritesAManagerMayMake = new()
    {
        // The manager's own account and devices.
        ["AuthController.*"] = "signing in and out, passkeys, the manager's own password",
        ["PasswordController.*"] = "the manager's own password",
        ["TwoFactorController.*"] = "the manager's own second factor",
        ["NotificationController.*"] = "marking the manager's own notifications read",
        ["PushNotificationController.RegisterDevice"] = "the manager's own handset",
        ["PushNotificationController.UnregisterDevice"] = "the manager's own handset",
        ["PushNotificationController.TestPush"] = "a test push to the manager's own handset",
        ["VanSalesCompatibilityController.ChangePassword"] = "the handset user's own password",
        // Reads sent as a POST because the question is a body.
        ["DesktopIntegrationController.ValidateInvoice"] = "checks an invoice, raises nothing",
        ["DesktopIntegrationController.ValidateStockAvailability"] = "checks stock, raises nothing",
        ["DesktopIntegrationController.ValidateTransfer"] = "checks a transfer, raises nothing",
        ["StockController.GetSalesInWarehousePost"] = "reads a warehouse's sales",
        ["VanSalesCompatibilityController.GetOrderHistory"] = "reads order history",
        ["VanSalesCompatibilityController.GetSalesOrderHistory"] = "reads sales order history",
        ["DesktopIntegrationController.RefreshWarehouseStock"] = "re-reads SAP stock into the snapshot the page shows",
        // Report emails, kept on 2026-10-28: a report emailed is a report read somewhere else.
        ["DesktopIntegrationController.EmailEndOfDayReport"] = "report email",
        ["DesktopIntegrationController.SendDesktopSalesReviewEmail"] = "report email",
        ["ReportController.SendSalesPerformanceEmail"] = "report email",
        // Printing a document that exists. Emailing, attaching, removing and signing ask
        // DocumentAttachmentAccessService, which refuses a manager every write (pinned below).
        ["DocumentController.GenerateDocument"] = "renders an existing document",
        ["DocumentController.GenerateAndDownloadDocument"] = "renders an existing document",
        ["DocumentController.EmailDocument"] = "refused in the handler: it asks for write access",
        ["DocumentController.UploadAttachment"] = "refused in the handler",
        ["DocumentController.DeleteAttachment"] = "refused in the handler",
        ["DocumentController.CreateSignature"] = "refused in the handler",
        // Van POD uploads ask the same attachment service, as a write.
        ["VanSalesCompatibilityController.UploadPod"] = "refused in the handler",
        ["VanSalesCompatibilityController.UploadPodFile"] = "refused in the handler",
        ["VanSalesCompatibilityController.UploadInvoicePodFile"] = "refused in the handler",
    };

    /// <summary>
    /// Every API write, read off the compiled controllers. A write a manager passes must be one of
    /// <see cref="WritesAManagerMayMake"/>; adding a write that admits a manager fails here until it is
    /// either closed to the manager or listed with its reason.
    /// </summary>
    [Fact]
    public async Task No_api_write_admits_a_manager_beyond_the_listed_ones()
    {
        var admitted = new List<string>();
        // [AllowAnonymous] writes (sign-in, payment callbacks, webhooks) are nobody's in particular.
        foreach (var (controller, action) in WriteActions()
            .Where(write => !write.Action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any()))
        {
            if (await ManagerPasses(controller, action))
            {
                admitted.Add($"{controller.Name}.{action.Name}");
            }
        }

        bool Listed(string key) =>
            WritesAManagerMayMake.ContainsKey(key) ||
            WritesAManagerMayMake.ContainsKey(key[..key.IndexOf('.')] + ".*");

        var unlisted = admitted.Where(key => !Listed(key)).Order().ToList();
        Assert.True(unlisted.Count == 0, "Writes a manager can make: " + string.Join(", ", unlisted));

        // A listed write that no longer admits a manager is a stale entry.
        var stale = WritesAManagerMayMake.Keys
            .Where(key => !key.EndsWith(".*", StringComparison.Ordinal) && !admitted.Contains(key)).Order().ToList();
        Assert.True(stale.Count == 0, "Listed writes that no longer admit a manager: " + string.Join(", ", stale));
    }

    /// <summary>The negative control for the sweep: it does see the writes a cashier and a stock controller make.</summary>
    [Fact]
    public async Task The_sweep_sees_writes_other_roles_make()
    {
        var transfer = typeof(InventoryTransferController).GetMethod(nameof(InventoryTransferController.DecidePendingInventoryTransfer))!;
        Assert.True(await RolePasses(typeof(InventoryTransferController), transfer, ApplicationRoles.StockController));
        Assert.False(await RolePasses(typeof(InventoryTransferController), transfer, ApplicationRoles.Manager));

        var writeOff = typeof(StockWriteOffController).GetMethods().Single(method => method.Name == "Create");
        Assert.True(await RolePasses(typeof(StockWriteOffController), writeOff, ApplicationRoles.StockController));
        Assert.False(await RolePasses(typeof(StockWriteOffController), writeOff, ApplicationRoles.Manager));
    }

    private static IEnumerable<(Type Controller, MethodInfo Action)> WriteActions() =>
        typeof(InventoryTransferController).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                    .SelectMany(verb => verb.HttpMethods)
                    .Any(verb => verb is "POST" or "PUT" or "PATCH" or "DELETE"))
                .Select(method => (type, method)));

    private static Task<bool> ManagerPasses(Type controller, MethodInfo action) =>
        RolePasses(controller, action, ApplicationRoles.Manager);

    /// <summary>
    /// Whether a user holding only <paramref name="role"/> and its default permissions gets through
    /// the action's role gates, named policies and permission filters. [AllowAnonymous] passes.
    /// </summary>
    private static async Task<bool> RolePasses(Type controller, MethodInfo action, string role)
    {
        var authorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .ToList();

        if (action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
        {
            return true;
        }

        foreach (var gate in authorize)
        {
            if (!string.IsNullOrWhiteSpace(gate.Roles) &&
                !gate.Roles.Split(',', StringSplitOptions.TrimEntries).Contains(role))
            {
                return false;
            }

            switch (gate.Policy)
            {
                case null or "ApiAccess" or "ApiAccessWithOperator":
                    break;
                // AdminOnly asks for system.admin; the other two are an integration's key and the
                // customer app's token, never a staff login.
                case "AdminOnly":
                    if (!Permission.GetDefaultPermissionsForRole(role).Contains(Permission.SystemAdmin)) return false;
                    break;
                case "ServiceKeyOnly" or "VanSalesCustomerAccess":
                    return false;
                default:
                    Assert.Fail($"{controller.Name}.{action.Name} uses policy {gate.Policy}, which this test does not know.");
                    break;
            }
        }

        return await PassesPermissions(controller, action, role);
    }

    /// <summary>
    /// The roles a compiled Web page admits, Admin included, read off its [Authorize] attribute. Unlike
    /// <see cref="WebPageGatePermissionAlignmentTests.PageRoles"/>, an Admin-only page is a valid answer.
    /// </summary>
    private static string[] GateRoles(string page)
    {
        var type = typeof(ShopInventory.Web.Data.UserRoles).Assembly.GetType($"ShopInventory.Web.Components.Pages.{page}");
        Assert.NotNull(type);

        var authorize = type!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single();
        Assert.False(string.IsNullOrWhiteSpace(authorize.Roles), $"{page} carries no role gate.");
        return authorize.Roles!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The roles every [Authorize(Roles)] on the controller and the action demand. Several attributes all
    /// apply, so a role must appear in each; the intersection is what gets through.
    /// </summary>
    private static string[] ActionRoles(Type controller, string actionName)
    {
        var action = controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == actionName);

        var gates = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
            .Select(attribute => attribute.Roles!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        return gates.Count == 0 ? [] : gates.Aggregate((left, right) => left.Intersect(right).ToArray());
    }
}
