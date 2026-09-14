using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending.Queries.ReadVendorImportFile;

/// <summary>The vendor rows in an uploaded template, as typed. Checking them is the API's job.</summary>
public sealed record ReadVendorImportFileQuery(byte[] Content) : IRequest<ErrorOr<List<ImportVendorRowModel>>>;
