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
/// A van rep's role is <c>Sales</c>, and the van POD routes end in the same <c>UploadPodCommand</c> as
/// the drivers' app, which asks <see cref="DocumentAttachmentAccessService"/> before storing anything.
/// </summary>
/// <remarks>
/// <see cref="VanSalesPodFileUploadTests"/> fakes the mediator, so it never reaches this check. Until
/// 2026-09-11 the handset's API key bypassed it anyway; once a user token was held to its own role,
/// every van upload was refused with "You do not have access to invoice attachments." A van rep is
/// held to the driver's rule: file against any invoice, open and remove only what it filed.
/// </remarks>
public sealed class VanRepInvoicePodAccessTests
{
    private const int DocEntry = 2392456;

    [Fact]
    public async Task A_van_rep_may_file_a_pod_against_an_invoice()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var rep = NewUser(ApplicationRoles.Sales, "van-rep");
        context.Users.Add(rep);
        await context.SaveChangesAsync();

        var result = await AccessService(context, rep).AuthorizeEntityAccessAsync(
            "Invoice", DocEntry, isWriteOperation: true, CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_van_rep_may_open_a_pod_it_filed()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var rep = NewUser(ApplicationRoles.Sales, "van-rep");
        context.Users.Add(rep);
        context.DocumentAttachments.Add(Attachment(901, rep.Id));
        await context.SaveChangesAsync();

        var result = await AccessService(context, rep).AuthorizeAttachmentAccessAsync(
            901, isWriteOperation: false, CancellationToken.None);

        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_van_rep_may_not_open_or_remove_someone_elses_pod(bool isWriteOperation)
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var rep = NewUser(ApplicationRoles.Sales, "van-rep");
        var driver = NewUser("Driver", "driver");
        context.Users.AddRange(rep, driver);
        context.DocumentAttachments.Add(Attachment(902, driver.Id));
        await context.SaveChangesAsync();

        var result = await AccessService(context, rep).AuthorizeAttachmentAccessAsync(
            902, isWriteOperation, CancellationToken.None);

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
