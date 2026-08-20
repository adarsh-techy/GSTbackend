using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/ewaybill")]
public class EWayBillController : ControllerBase
{
    private readonly IEWayBillService _ewbService;

    public EWayBillController(IEWayBillService ewbService)
    {
        _ewbService = ewbService;
    }

    [HttpPost("generate/{invoiceId:guid}")]
    public async Task<ActionResult<EWayBillResponse>> Generate(
        Guid invoiceId,
        [FromBody] GenerateEWayBillRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var ewb = await _ewbService.GenerateAsync(invoiceId, request, cancellationToken);
            return Ok(ewb);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    // Generate by CarolERP BillId — used by tenants whose invoices are read
    // live from CarolERP and don't have a row in the tenant Invoices table.
    [HttpPost("generate/bill/{billId:int}")]
    public async Task<ActionResult<EWayBillResponse>> GenerateForBill(
        int billId,
        [FromBody] GenerateEWayBillRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var ewb = await _ewbService.GenerateForBillAsync(billId, request, cancellationToken);
            return Ok(ewb);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("cancel/{ewbId:guid}")]
    public async Task<ActionResult<EWayBillResponse>> Cancel(
        Guid ewbId,
        [FromBody] CancelEWayBillRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var ewb = await _ewbService.CancelAsync(ewbId, request?.Reason ?? string.Empty, cancellationToken);
            return Ok(ewb);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("update-vehicle/{ewbId:guid}")]
    public async Task<ActionResult<EWayBillResponse>> UpdateVehicle(
        Guid ewbId,
        [FromBody] UpdateVehicleRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var ewb = await _ewbService.UpdateVehicleAsync(ewbId, request?.VehicleNumber ?? string.Empty, cancellationToken);
            return Ok(ewb);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpGet("status/{invoiceId:guid}")]
    public async Task<ActionResult<EWayBillResponse>> Status(Guid invoiceId, CancellationToken cancellationToken)
    {
        var ewb = await _ewbService.GetByInvoiceAsync(invoiceId, cancellationToken);
        return ewb is null ? NotFound($"No E-Way Bill found for invoice {invoiceId}.") : Ok(ewb);
    }

    [HttpGet("list")]
    public async Task<ActionResult<IReadOnlyList<EWayBillResponse>>> List(CancellationToken cancellationToken)
    {
        var rows = await _ewbService.ListAsync(cancellationToken);
        return Ok(rows);
    }
}
