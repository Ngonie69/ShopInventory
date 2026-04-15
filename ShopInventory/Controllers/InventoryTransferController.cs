using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;
using ShopInventory.Features.InventoryTransfers.Commands.CreateTransferRequest;
using ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;
using ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransfers;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferByDocEntry;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestByDocEntry;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestsByWarehouse;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDate;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDateRange;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByWarehouse;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class InventoryTransferController(IMediator mediator) : ApiControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(InventoryTransferCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateInventoryTransfer(
        [FromBody] CreateInventoryTransferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateInventoryTransferCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetInventoryTransferByDocEntry), new { docEntry = value.Transfer!.DocEntry }, value),
            errors => Problem(errors));
    }

    [HttpGet("{warehouseCode}")]
    [ProducesResponseType(typeof(InventoryTransferListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInventoryTransfersByWarehouse(string warehouseCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransfersByWarehouseQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{warehouseCode}/paged")]
    [ProducesResponseType(typeof(InventoryTransferListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPagedInventoryTransfers(
        string warehouseCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPagedTransfersQuery(warehouseCode, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{warehouseCode}/date/{date}")]
    [ProducesResponseType(typeof(InventoryTransferDateResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInventoryTransfersByDate(string warehouseCode, string date, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransfersByDateQuery(warehouseCode, date), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{warehouseCode}/daterange")]
    [ProducesResponseType(typeof(InventoryTransferDateResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInventoryTransfersByDateRange(
        string warehouseCode, [FromQuery] string fromDate, [FromQuery] string toDate,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTransfersByDateRangeQuery(warehouseCode, fromDate, toDate, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("detail/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInventoryTransferByDocEntry(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #region Transfer Request Endpoints

    [HttpPost("request")]
    [ProducesResponseType(typeof(TransferRequestCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTransferRequest(
        [FromBody] CreateTransferRequestDto request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateTransferRequestCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetTransferRequestByDocEntry), new { docEntry = value.TransferRequest!.DocEntry }, value),
            errors => Problem(errors));
    }

    [HttpPost("request/{docEntry:int}/convert")]
    [ProducesResponseType(typeof(TransferRequestConvertedResponseDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> ConvertTransferRequestToTransfer(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ConvertTransferRequestCommand(docEntry), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetInventoryTransferByDocEntry), new { docEntry = value.Transfer!.DocEntry }, value),
            errors => Problem(errors));
    }

    [HttpPost("request/{docEntry:int}/close")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CloseTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CloseTransferRequestCommand(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("request/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransferRequestByDocEntry(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferRequestByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("requests/{warehouseCode}")]
    [ProducesResponseType(typeof(TransferRequestListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransferRequestsByWarehouse(string warehouseCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferRequestsByWarehouseQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("requests")]
    [ProducesResponseType(typeof(TransferRequestListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPagedTransferRequests(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPagedTransferRequestsQuery(page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion
}
