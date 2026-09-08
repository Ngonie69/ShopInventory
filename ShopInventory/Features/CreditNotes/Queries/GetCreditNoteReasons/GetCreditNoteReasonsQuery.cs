using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteReasons;

/// <summary>
/// The reasons a credit note may be raised for, as the running SAP company database defines them.
/// </summary>
public sealed record GetCreditNoteReasonsQuery : IRequest<ErrorOr<GetCreditNoteReasonsResult>>;
