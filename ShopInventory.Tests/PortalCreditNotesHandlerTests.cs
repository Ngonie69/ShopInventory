using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Features.CustomerPortal.Queries.DownloadPortalCreditNotePdf;
using ShopInventory.Web.Features.CustomerPortal.Queries.GetPortalCreditNotes;
using ShopInventory.Web.Models;
using WebCreditNoteService = ShopInventory.Web.Services.ICreditNoteService;
using WebLinkedAccounts = ShopInventory.Web.Services.ICustomerLinkedAccountService;

namespace ShopInventory.Tests;

/// <summary>
/// The portal's side of the credit notes page. The portal calls the API with the integration key, so
/// the API cannot tell whose page it is answering; these handlers are where the login's own accounts
/// decide what is shown and what may be downloaded.
/// </summary>
public sealed class PortalCreditNotesHandlerTests
{
    private const string Portal = "C20417";
    private const string SubAccount = "C20417Z";

    [Fact]
    public async Task EveryLinkedAccountIsAskedForAndNothingElseIsShown()
    {
        IReadOnlyCollection<string>? askedFor = null;
        var creditNotes = StubProxy.For<WebCreditNoteService>((method, args) =>
        {
            Assert.Equal(nameof(WebCreditNoteService.GetCustomerCreditNotesAsync), method.Name);
            askedFor = (IReadOnlyCollection<string>)args![0]!;
            return Task.FromResult<CustomerCreditNotesResponse?>(new CustomerCreditNotesResponse
            {
                CreditNotes =
                [
                    new CustomerCreditNote { DocNum = 30377, CardCode = Portal },
                    new CustomerCreditNote { DocNum = 30402, CardCode = SubAccount.ToLowerInvariant() },
                    new CustomerCreditNote { DocNum = 30999, CardCode = "C99001" }
                ]
            });
        });

        var handler = new GetPortalCreditNotesHandler(
            LinkedAccounts(Portal, SubAccount), creditNotes, NullLogger<GetPortalCreditNotesHandler>.Instance);

        var result = await handler.Handle(
            new GetPortalCreditNotesQuery(Portal, new DateTime(2026, 10, 1), new DateTime(2026, 11, 6)),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal([Portal, SubAccount], askedFor!);
        Assert.Equal([30377, 30402], result.Value.Select(note => note.DocNum));
    }

    [Fact]
    public async Task AnUnansweredListIsAnErrorNotAnEmptyPeriod()
    {
        var creditNotes = StubProxy.For<WebCreditNoteService>((_, _) => Task.FromResult<CustomerCreditNotesResponse?>(null));
        var handler = new GetPortalCreditNotesHandler(
            LinkedAccounts(Portal), creditNotes, NullLogger<GetPortalCreditNotesHandler>.Instance);

        var result = await handler.Handle(
            new GetPortalCreditNotesQuery(Portal, new DateTime(2026, 10, 1), new DateTime(2026, 11, 6)),
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ACreditNoteOnAnAccountTheLoginDoesNotReachIsRefusedBeforeTheApiIsAsked()
    {
        var handler = new DownloadPortalCreditNotePdfHandler(
            LinkedAccounts(Portal, SubAccount),
            StubProxy.Unused<WebCreditNoteService>(),
            NullLogger<DownloadPortalCreditNotePdfHandler>.Instance);

        var result = await handler.Handle(new DownloadPortalCreditNotePdfQuery(Portal, 61377, "C99001"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorOr.ErrorType.Forbidden, result.FirstError.Type);
    }

    [Fact]
    public async Task ASubAccountsCreditNoteIsFetchedForThatAccount()
    {
        (int DocEntry, string CardCode)? asked = null;
        var creditNotes = StubProxy.For<WebCreditNoteService>((method, args) =>
        {
            Assert.Equal(nameof(WebCreditNoteService.GetCreditNotePdfAsync), method.Name);
            asked = ((int)args![0]!, (string)args[1]!);
            return Task.FromResult<byte[]?>([0x25, 0x50, 0x44, 0x46]);
        });

        var handler = new DownloadPortalCreditNotePdfHandler(
            LinkedAccounts(Portal, SubAccount), creditNotes, NullLogger<DownloadPortalCreditNotePdfHandler>.Instance);

        var result = await handler.Handle(new DownloadPortalCreditNotePdfQuery(Portal, 61402, SubAccount), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal((61402, SubAccount), asked);
    }

    private static WebLinkedAccounts LinkedAccounts(params string[] cardCodes) =>
        StubProxy.For<WebLinkedAccounts>((method, _) =>
        {
            Assert.Equal(nameof(WebLinkedAccounts.GetAllCardCodesAsync), method.Name);
            return Task.FromResult(cardCodes.ToList());
        });
}
