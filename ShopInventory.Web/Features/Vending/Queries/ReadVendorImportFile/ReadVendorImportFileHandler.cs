using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending.Queries.ReadVendorImportFile;

public sealed class ReadVendorImportFileHandler
    : IRequestHandler<ReadVendorImportFileQuery, ErrorOr<List<ImportVendorRowModel>>>
{
    public Task<ErrorOr<List<ImportVendorRowModel>>> Handle(
        ReadVendorImportFileQuery request,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.Content, writable: false);
        return Task.FromResult(VendorImportWorkbook.Read(stream));
    }
}
