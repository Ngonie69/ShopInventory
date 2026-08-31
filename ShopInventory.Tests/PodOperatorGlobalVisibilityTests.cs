using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Features.Invoices.Queries.GetAllPods;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A POD operator is a company-wide oversight role. AssignedSection still records where the
/// account works and still limits destructive attachment operations, but it must not hide uploads.
/// </summary>
public sealed class PodOperatorGlobalVisibilityTests
{
    [Theory]
    [InlineData("PodOperator", null)]
    [InlineData("Operator", "Factory")]
    public async Task Product_pod_list_only_scopes_the_legacy_operator_role(
        string role,
        string? expectedAssignedSection)
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var user = NewUser(role);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        string? capturedAssignedSection = "not-called";
        var documentService = StubProxy.For<IDocumentService>((method, args) =>
        {
            if (method.Name != nameof(IDocumentService.GetAllPodAttachmentsAsync))
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            capturedAssignedSection = (string?)args![9];
            return Task.FromResult(new PodAttachmentListResponseDto
            {
                Items = [],
                Page = 1,
                PageSize = 20
            });
        });

        var handler = new GetAllPodsHandler(context, documentService);
        var result = await handler.Handle(
            new GetAllPodsQuery(1, 20, null, null, null, null, null, null, null, user.Id),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(expectedAssignedSection, capturedAssignedSection);
    }

    [Fact]
    public async Task Pod_operator_can_open_an_invoice_pod_uploaded_outside_their_location()
    {
        using var connection = OpenDatabase();
        await using var context = CreateContext(connection);
        var podOperator = NewUser("PodOperator");
        var otherUploader = NewUser("Driver", "other-driver");
        context.Users.AddRange(podOperator, otherUploader);
        context.DocumentAttachments.Add(new DocumentAttachmentEntity
        {
            Id = 741,
            EntityType = "Invoice",
            EntityId = 99102,
            FileName = "POD_99102.pdf",
            StoredFileName = "outside-location/POD_99102.pdf",
            UploadedByUserId = otherUploader.Id
        });
        await context.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, podOperator.Id.ToString()),
            new Claim(ClaimTypes.Name, podOperator.Username),
            new Claim(ClaimTypes.Role, podOperator.Role)
        ], "test"));

        var accessService = new DocumentAttachmentAccessService(
            context,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } },
            StubProxy.Unused<IUserManagementService>(),
            StubProxy.Unused<IDocumentService>(),
            NullLogger<DocumentAttachmentAccessService>.Instance);

        var result = await accessService.AuthorizeAttachmentAccessAsync(
            741,
            isWriteOperation: false,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(741, result.Value.Id);
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

    private static User NewUser(string role, string username = "pod-operator") => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = "not-used",
        Role = role,
        AssignedSection = "Factory",
        IsActive = true
    };
}
