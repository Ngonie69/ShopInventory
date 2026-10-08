using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using ShopInventory.Controllers;
using ShopInventory.Migrations;
using ShopInventory.Models;
using static ShopInventory.Tests.WebPageGatePermissionAlignmentTests;

namespace ShopInventory.Tests;

/// <summary>
/// A manager runs the business and leaves set-up and administration of the system to the administrator.
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
    ];

    [Theory]
    [MemberData(nameof(WithheldFromManager))]
    public void A_manager_holds_no_administration_or_document_raising_permission(string code)
    {
        Assert.DoesNotContain(code, Permission.GetDefaultPermissionsForRole(ApplicationRoles.Manager));
    }

    /// <summary>The business a manager reads, approves and oversees, which the split must not take away.</summary>
    [Theory]
    [InlineData(Permission.ViewInvoices)]
    [InlineData(Permission.ViewPayments)]
    [InlineData(Permission.ViewSalesOrders)]
    [InlineData(Permission.ApproveSalesOrders)]
    [InlineData(Permission.ViewQuotations)]
    [InlineData(Permission.ApproveSapCreditNotes)]
    [InlineData(Permission.AddApprovedCreditNotes)]
    [InlineData(Permission.ApprovePurchaseOrders)]
    [InlineData(Permission.ViewReports)]
    [InlineData(Permission.ViewStock)]
    [InlineData(Permission.TransferStock)]
    [InlineData(Permission.ViewVanSalesAttendance)]
    [InlineData(Permission.FulfilVanSalesCustomerOrders)]
    [InlineData(Permission.ConfirmMarketBreakages)]
    public void A_manager_keeps_the_business(string code)
    {
        Assert.Contains(code, Permission.GetDefaultPermissionsForRole(ApplicationRoles.Manager));
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
