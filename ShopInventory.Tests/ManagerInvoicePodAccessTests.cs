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
