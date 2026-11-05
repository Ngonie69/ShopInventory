using System.Net;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Errors;
using ShopInventory.Controllers;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP refuses to add a credit memo draft that carries an attachment while its Service Layer cannot
/// reach the attachments folder. These pin down which SAP answers count as that refusal, and that the
/// sentence the API writes is the one the Credit Note Approvals page shows.
/// </summary>
public sealed class CreditNoteAttachmentsFolderRefusalTests
{
    [Theory]
    [InlineData("Attachments folder not defined, or Attachments folder has been changed or removed [Message 131-102]", true)]
    [InlineData("Ordner fuer Anlagen nicht definiert [Meldung 131-102]", true)]
    [InlineData("attachments folder not defined", true)]
    [InlineData("Document is locked by another user", false)]
    [InlineData("Internal error (-5002) occurred", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_attachments_folder_refusal_is_recognised(string? sapMessage, bool expected)
    {
        Assert.Equal(expected, CreditNoteDraftAttachments.IsAttachmentsFolderRefusal(sapMessage));
    }

    /// <summary>
    /// The Web reads the refusal through <see cref="ApiErrorResponse.GetFriendlyMessage(HttpStatusCode?, string?, string, string?, string?)"/>,
    /// which strips "Failed to ...:" style prefixes. The whole sentence must survive it, workaround included.
    /// </summary>
    [Fact]
    public void The_page_shows_the_whole_explanation()
    {
        var error = Errors.CreditNoteApproval.AddBlockedByAttachmentsFolder;
        var controller = new RefusingController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Request = { Path = "/api/credit-note-approvals/3110/add" } }
            }
        };

        var result = Assert.IsAssignableFrom<ObjectResult>(controller.Refuse([error]));
        var body = JsonSerializer.Serialize(result.Value, result.Value!.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(
            error.Description,
            ApiErrorResponse.GetFriendlyMessage(HttpStatusCode.BadRequest, body, "The credit note could not be added in SAP."));
    }

    private sealed class RefusingController : ApiControllerBase
    {
        public IActionResult Refuse(List<Error> errors) => Problem(errors);
    }
}
