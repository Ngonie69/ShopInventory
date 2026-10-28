using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.Documents;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A manager views PODs and files none, under the 2026-10-05 view-only rule.
/// </summary>
/// <remarks>
/// <c>InvoiceController</c> lets Manager reach an invoice's attachment list and its downloads, but
/// <see cref="DocumentAttachmentAccessService"/> then refused the role outright with "You do not have
/// access to invoice attachments.", so the portal's POD pages listed notes a manager could not open.
/// Reading is allowed on any invoice; uploading and removing stay refused.
/// </remarks>
public sealed class ManagerInvoicePodAccessTests
{
    private const int DocEntry = 2392456;

    [Fact]
    public async Task A_manager_may_list_an_invoices_attachments()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var manager = NewUser(ApplicationRoles.Manager, "manager");
        context.Users.Add(manager);
        await context.SaveChangesAsync();

        var result = await AccessService(context, manager).AuthorizeEntityAccessAsync(
            "Invoice", DocEntry, isWriteOperation: false, CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_manager_may_open_a_pod_someone_else_filed()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var manager = NewUser(ApplicationRoles.Manager, "manager");
        var driver = NewUser(ApplicationRoles.Driver, "driver");
        context.Users.AddRange(manager, driver);
        context.DocumentAttachments.Add(Attachment(911, driver.Id));
        await context.SaveChangesAsync();

        var result = await AccessService(context, manager).AuthorizeAttachmentAccessAsync(
            911, isWriteOperation: false, CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_manager_may_not_file_a_pod()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var manager = NewUser(ApplicationRoles.Manager, "manager");
        context.Users.Add(manager);
        await context.SaveChangesAsync();

        var result = await AccessService(context, manager).AuthorizeEntityAccessAsync(
            "Invoice", DocEntry, isWriteOperation: true, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task A_manager_may_not_remove_a_pod()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var manager = NewUser(ApplicationRoles.Manager, "manager");
        context.Users.Add(manager);
        context.DocumentAttachments.Add(Attachment(912, manager.Id));
        await context.SaveChangesAsync();

        var result = await AccessService(context, manager).AuthorizeAttachmentAccessAsync(
            912, isWriteOperation: true, CancellationToken.None);

        Assert.True(result.IsError);
    }

    /// <summary>
    /// A document type with no rule of its own falls through to "Admin or Manager", which let a
    /// manager attach to and remove from any of them until the role became read-only (2026-10-28).
    /// </summary>
    [Theory]
    [InlineData("SalesOrder")]
    [InlineData("PurchaseOrder")]
    [InlineData("ExternalPurchaseOrder")]
    public async Task A_manager_reads_any_documents_attachments_and_writes_none(string entityType)
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var manager = NewUser(ApplicationRoles.Manager, "manager");
        context.Users.Add(manager);
        await context.SaveChangesAsync();

        var service = AccessService(context, manager);
        var write = await service.AuthorizeEntityAccessAsync(entityType, 7, isWriteOperation: true, CancellationToken.None);

        Assert.True(write.IsError);
        Assert.Contains("Managers can view", write.FirstError.Description);
    }

    /// <summary>The control: the same write on the same fall-through type is the administrator's.</summary>
    [Fact]
    public async Task An_admin_still_attaches_to_a_sales_order()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var admin = NewUser(ApplicationRoles.Admin, "admin");
        context.Users.Add(admin);
        await context.SaveChangesAsync();

        var write = await AccessService(context, admin).AuthorizeEntityAccessAsync(
            "SalesOrder", 7, isWriteOperation: true, CancellationToken.None);

        Assert.False(write.IsError);
    }

    /// <summary>Signing a document is judged as a read of it, so the signature handler asks this as well.</summary>
    [Fact]
    public async Task A_manager_may_not_sign_and_an_admin_may()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var manager = NewUser(ApplicationRoles.Manager, "manager");
        var admin = NewUser(ApplicationRoles.Admin, "admin");
        context.Users.AddRange(manager, admin);
        await context.SaveChangesAsync();

        Assert.True((await AccessService(context, manager).RefuseReadOnlyManagerAsync(CancellationToken.None)).IsError);
        Assert.False((await AccessService(context, admin).RefuseReadOnlyManagerAsync(CancellationToken.None)).IsError);
    }

    private static DocumentAttachmentEntity Attachment(int id, Guid uploadedBy) => new()
    {
        Id = id,
        EntityType = "Invoice",
        EntityId = DocEntry,
        FileName = $"POD_{DocEntry}.jpg",
        StoredFileName = $"pods/POD_{DocEntry}_{id}.jpg",
        UploadedByUserId = uploadedBy
    };

    private static DocumentAttachmentAccessService AccessService(ApplicationDbContext context, User user)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role)
        ], "test"));

        return new DocumentAttachmentAccessService(
            context,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } },
            StubProxy.Unused<IUserManagementService>(),
            StubProxy.Unused<IDocumentService>(),
            NullLogger<DocumentAttachmentAccessService>.Instance);
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateContext(connection);
        context.Database.EnsureCreated();
        return connection;
    }

    private static ApplicationDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);

    private static User NewUser(string role, string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = "not-used",
        Role = role,
        IsActive = true
    };
}
