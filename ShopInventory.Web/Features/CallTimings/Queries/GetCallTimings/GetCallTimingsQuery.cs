using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CallTimings.Queries.GetCallTimings;

/// <param name="Window"><c>15m</c>, <c>1h</c> or <c>today</c>.</param>
public sealed record GetCallTimingsQuery(string Window) : IRequest<ErrorOr<CallTimingsModel>>;
