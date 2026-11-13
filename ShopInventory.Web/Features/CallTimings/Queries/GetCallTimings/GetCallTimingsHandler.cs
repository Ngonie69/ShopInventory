using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CallTimings.Queries.GetCallTimings;

public sealed class GetCallTimingsHandler(
    ICallTimingsService callTimings) : IRequestHandler<GetCallTimingsQuery, ErrorOr<CallTimingsModel>>
{
    public async Task<ErrorOr<CallTimingsModel>> Handle(GetCallTimingsQuery request, CancellationToken cancellationToken)
    {
        var timings = await callTimings.GetAsync(request.Window, cancellationToken);

        // The timings are the API's own memory, so a null here is the call to the API failing, not SAP.
        return timings is null
            ? Errors.CallTimings.LoadFailed(
                "The API did not answer for its call timings. This says nothing about SAP: it is the call "
                + "to the API that failed.")
            : timings;
    }
}
