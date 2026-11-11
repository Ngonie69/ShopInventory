using System.Text.Json;
using ApiDtos = ShopInventory.DTOs;
using WebModels = ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The approvals list item crosses from the API to the Web as JSON into a hand-written mirror. A field
/// the mirror drops, or types differently, never reaches the page, and nothing else would notice:
/// the already-credited flag would simply never show and Approve would come back.
/// </summary>
public sealed class CreditNoteApprovalContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Fact]
    public void An_already_credited_row_reaches_the_web_with_its_credit_note_and_without_approve()
    {
        var sent = new ApiDtos.CreditNoteApprovalDetailDto
        {
            Request = new ApiDtos.CreditNoteApprovalListItemDto
            {
                Code = 84190,
                Status = "Pending",
                CanDecide = true,
                CanApprove = false,
                CanAdd = false,
                StatusNote = "Already credited: credit note 61455 …",
                AlreadyCredited = new ApiDtos.CreditNoteApprovalAlreadyCreditedDto
                {
                    DocEntry = 70310,
                    DocNum = 61455,
                    EnteredDate = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
                    EnteredByUserCode = "AR1",
                    EnteredByName = "Accounts Clerk"
                }
            }
        };

        var received = JsonSerializer.Deserialize<WebModels.CreditNoteApprovalDetailDto>(JsonSerializer.Serialize(sent, Wire), Wire)!.Request;

        Assert.True(received.CanDecide);
        Assert.False(received.CanApprove);
        Assert.Equal(sent.Request.StatusNote, received.StatusNote);
        Assert.NotNull(received.AlreadyCredited);
        Assert.Equal(70310, received.AlreadyCredited!.DocEntry);
        Assert.Equal(61455, received.AlreadyCredited.DocNum);
        Assert.Equal(new DateTime(2026, 8, 15), received.AlreadyCredited.EnteredDate!.Value.Date);
        Assert.Equal("AR1", received.AlreadyCredited.EnteredByUserCode);
        Assert.Equal("Accounts Clerk", received.AlreadyCredited.EnteredByName);
    }

    /// <summary>The list names nobody, and most rows match nothing: both nulls must cross too.</summary>
    [Fact]
    public void A_row_with_nothing_already_credited_keeps_approve()
    {
        var sent = new ApiDtos.CreditNoteApprovalListResponseDto
        {
            Items =
            [
                new ApiDtos.CreditNoteApprovalListItemDto { Code = 1, Status = "Pending", CanDecide = true, CanApprove = true },
                new ApiDtos.CreditNoteApprovalListItemDto
                {
                    Code = 2,
                    Status = "Approved",
                    AlreadyCredited = new ApiDtos.CreditNoteApprovalAlreadyCreditedDto { DocEntry = 5, DocNum = 6 }
                }
            ]
        };

        var received = JsonSerializer.Deserialize<WebModels.CreditNoteApprovalListResponseDto>(JsonSerializer.Serialize(sent, Wire), Wire)!;

        Assert.True(received.Items[0].CanApprove);
        Assert.Null(received.Items[0].AlreadyCredited);
        Assert.Null(received.Items[1].AlreadyCredited!.EnteredByUserCode);
        Assert.Null(received.Items[1].AlreadyCredited!.EnteredDate);
    }
}
