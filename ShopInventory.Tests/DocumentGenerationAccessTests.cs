using System.Security.Claims;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Authentication;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Features.Documents.Commands.CreateSignature;
using ShopInventory.Features.Documents.Commands.EmailDocument;
using ShopInventory.Features.Documents.Commands.GenerateAndDownloadDocument;
using ShopInventory.Features.Documents.Commands.GenerateDocument;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Generating, downloading, emailing and signing a document ask <see cref="DocumentAttachmentAccessService"/>
/// about the entity in the request before <see cref="IDocumentService"/> is reached.
/// </summary>
/// <remarks>
/// The four routes sit behind "ApiAccess" alone, because /documents/{type}/{id} admits every signed-in role
/// and a role gate would break it for most of them. Generating, downloading and signing need read access to
/// the entity; emailing sends the document and the entity's attachments to an outside address, so it needs
/// read and write access both. scripts/check_api_write_gates.py fails the build if a handler stops asking.
/// </remarks>
public sealed class DocumentGenerationAccessTests
{
    private const int InvoiceId = 2392456;
    private const int ScopedInvoiceId = 2392457;
    private const string Section = "Harare";

    public enum DocumentAction
    {
        Generate,
        Download,
        Email,
        Sign
    }

    private static bool IsRead(DocumentAction action) => action is DocumentAction.Generate or DocumentAction.Download;

    // Signing is judged as a read of the entity, but a manager, who is read-only, signs nothing.
    private static bool MaySign(string role, DocumentAction action) =>
        action != DocumentAction.Sign || role != ApplicationRoles.Manager;

    // Invoice: who may render it (generate, download, sign) and who may email it.
    [Theory]
    [InlineData(ApplicationRoles.Admin, true, true)]
    [InlineData(ApplicationRoles.Cashier, true, true)]
    [InlineData(ApplicationRoles.SalesRep, true, true)]
    [InlineData(ApplicationRoles.PodOperator, true, true)]
    [InlineData(ApplicationRoles.Manager, true, false)]
    [InlineData(ApplicationRoles.Driver, false, false)]
    [InlineData(ApplicationRoles.Sales, false, false)]
    [InlineData(ApplicationRoles.Merchandiser, false, false)]
    [InlineData(ApplicationRoles.TillOperator, false, false)]
    [InlineData(ApplicationRoles.StockController, false, false)]
    [InlineData(ApplicationRoles.Lab, false, false)]
    public async Task An_invoice_document_is_held_to_the_callers_invoice_access(string role, bool mayRender, bool mayEmail)
    {
        foreach (var action in Enum.GetValues<DocumentAction>())
        {
            var expected = action == DocumentAction.Email ? mayEmail : mayRender && MaySign(role, action);
            var outcome = await RunAsync(action, role, "Invoice", InvoiceId);

            Assert.True(expected == outcome.Admitted, $"{role} {action} Invoice: expected admitted={expected}");
            Assert.Equal(outcome.Admitted, outcome.ServiceCalled);
        }
    }

    [Theory]
    [InlineData(ScopedInvoiceId, true)]
    [InlineData(InvoiceId, false)]
    public async Task An_operator_reaches_only_invoices_in_its_section(int invoiceId, bool inSection)
    {
        foreach (var action in Enum.GetValues<DocumentAction>())
        {
            var outcome = await RunAsync(action, ApplicationRoles.Operator, "Invoice", invoiceId);

            Assert.True(inSection == outcome.Admitted, $"Operator {action} invoice {invoiceId}");
            Assert.Equal(outcome.Admitted, outcome.ServiceCalled);
        }
    }

    // A document type the access service has no rule for is open to Admin and Manager only, and to the
    // manager only to read: since 2026-10-28 a manager renders and downloads, and neither emails nor signs.
    [Theory]
    [InlineData(ApplicationRoles.Admin, true)]
    [InlineData(ApplicationRoles.Manager, true)]
    [InlineData(ApplicationRoles.Cashier, false)]
    [InlineData(ApplicationRoles.SalesRep, false)]
    [InlineData(ApplicationRoles.PodOperator, false)]
    [InlineData(ApplicationRoles.Driver, false)]
    [InlineData(ApplicationRoles.Merchandiser, false)]
    public async Task Any_other_document_type_is_for_admins_and_managers(string role, bool admitted)
    {
        foreach (var action in Enum.GetValues<DocumentAction>())
        {
            var expected = admitted && (role != ApplicationRoles.Manager || IsRead(action));
            var outcome = await RunAsync(action, role, "SalesOrder", 17);

            Assert.True(expected == outcome.Admitted, $"{role} {action} SalesOrder");
            Assert.Equal(outcome.Admitted, outcome.ServiceCalled);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_purchase_order_is_emailed_only_with_the_upload_permission(bool holdsUploadPermission)
    {
        string[] permissions = holdsUploadPermission ? [Permission.UploadPurchaseOrderDocuments] : [];

        foreach (var action in Enum.GetValues<DocumentAction>())
        {
            var expected = action != DocumentAction.Email || holdsUploadPermission;
            var outcome = await RunAsync(action, ApplicationRoles.Merchandiser, "ExternalPurchaseOrder", 41, permissions);

            Assert.True(expected == outcome.Admitted, $"Merchandiser {action} ExternalPurchaseOrder");
            Assert.Equal(outcome.Admitted, outcome.ServiceCalled);
        }
    }

    [Fact]
    public async Task A_refusal_is_forbidden_and_never_reaches_the_document_service()
    {
        foreach (var action in Enum.GetValues<DocumentAction>())
        {
            var outcome = await RunAsync(action, ApplicationRoles.Driver, "Invoice", InvoiceId);

            Assert.Equal(ErrorType.Forbidden, outcome.ErrorType);
            Assert.False(outcome.ServiceCalled);
        }
    }

    [Fact]
    public async Task An_integration_key_alone_is_not_held_to_a_role()
    {
        foreach (var action in Enum.GetValues<DocumentAction>())
        {
            var outcome = await RunAsync(action, role: null, "SalesOrder", 17);

            Assert.True(outcome.Admitted, $"service key {action}");
        }
    }

    [Fact]
    public async Task A_web_request_is_held_to_its_user_and_not_to_the_key_beside_it()
    {
        foreach (var action in Enum.GetValues<DocumentAction>())
        {
            var outcome = await RunAsync(action, ApplicationRoles.Driver, "Invoice", InvoiceId, withWebKey: true);

            Assert.False(outcome.Admitted, $"Driver with the Web key {action}");
        }
    }

    private sealed record Outcome(bool Admitted, ErrorType? ErrorType, bool ServiceCalled);

    /// <param name="role">The user's role, or null for an integration's key on its own.</param>
    private static async Task<Outcome> RunAsync(
        DocumentAction action,
        string? role,
        string documentType,
        int entityId,
        string[]? permissions = null,
        bool withWebKey = false)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync();

        var identities = new List<ClaimsIdentity>();
        if (role is not null)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = role.ToLowerInvariant(),
                PasswordHash = "not-used",
                Role = role,
                AssignedSection = Section,
                IsActive = true
            };
            context.Users.Add(user);
            await context.SaveChangesAsync();

            identities.Add(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            ], AuthenticationSchemes.Jwt));
        }

        if (role is null || withWebKey)
        {
            identities.Add(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "web"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin),
                new Claim(ClaimTypes.AuthenticationMethod, AuthenticationSchemes.ApiKey),
                new Claim("api_key_name", "web")
            ], AuthenticationSchemes.ApiKey));
        }

        var serviceCalled = false;
        var documentService = StubProxy.For<IDocumentService>((method, _) =>
        {
            switch (method.Name)
            {
                case nameof(IDocumentService.GetScopedPodInvoiceDocEntriesAsync):
                    return Task.FromResult(new List<int> { ScopedInvoiceId });
                case nameof(IDocumentService.GenerateDocumentAsync):
                case nameof(IDocumentService.EmailDocumentAsync):
                    serviceCalled = true;
                    return Task.FromResult(new GenerateDocumentResponseDto { Success = true, FileContent = [1] });
                case nameof(IDocumentService.CreateSignatureAsync):
                    serviceCalled = true;
                    return Task.FromResult(new DocumentSignatureDto());
                default:
                    throw new InvalidOperationException($"IDocumentService.{method.Name} was not expected to be called.");
            }
        });

        var userManagement = StubProxy.For<IUserManagementService>((method, _) =>
            method.Name == nameof(IUserManagementService.GetEffectivePermissionsAsync)
                ? Task.FromResult((permissions ?? []).ToList())
                : throw new InvalidOperationException($"IUserManagementService.{method.Name} was not expected to be called."));

        var accessService = new DocumentAttachmentAccessService(
            context,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identities) } },
            userManagement,
            documentService,
            NullLogger<DocumentAttachmentAccessService>.Instance);

        var generateRequest = new GenerateDocumentRequest { DocumentType = documentType, EntityId = entityId };
        IErrorOr result = action switch
        {
            DocumentAction.Generate => await new GenerateDocumentHandler(
                    accessService, documentService, NullLogger<GenerateDocumentHandler>.Instance)
                .Handle(new GenerateDocumentCommand(generateRequest, Guid.Empty), CancellationToken.None),
            DocumentAction.Download => await new GenerateAndDownloadDocumentHandler(
                    accessService, documentService, NullLogger<GenerateAndDownloadDocumentHandler>.Instance)
                .Handle(new GenerateAndDownloadDocumentCommand(generateRequest, Guid.Empty), CancellationToken.None),
            DocumentAction.Email => await new EmailDocumentHandler(
                    accessService, documentService, NullLogger<EmailDocumentHandler>.Instance)
                .Handle(new EmailDocumentCommand(
                    new EmailDocumentRequest { DocumentType = documentType, EntityId = entityId, RecipientEmail = "customer@example.com" },
                    Guid.Empty), CancellationToken.None),
            DocumentAction.Sign => await new CreateSignatureHandler(
                    accessService, documentService, NullLogger<CreateSignatureHandler>.Instance)
                .Handle(new CreateSignatureCommand(
                    new CreateSignatureRequest { DocumentType = documentType, DocumentId = entityId, SignerName = "Signer", SignatureData = "data:," },
                    null, "127.0.0.1", "test"), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        return new Outcome(
            !result.IsError,
            result.IsError ? result.Errors![0].Type : null,
            serviceCalled);
    }
}
