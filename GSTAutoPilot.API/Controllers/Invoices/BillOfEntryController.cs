using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/bill-of-entry")]
[Authorize]
public class BillOfEntryController : ControllerBase
{
    private readonly IBillOfEntryService _service;

    public BillOfEntryController(IBillOfEntryService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BillOfEntryDto>>> List([FromQuery] string period, CancellationToken cancellationToken)
    {
        try { return Ok(await _service.ListAsync(period, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost]
    public async Task<ActionResult<BillOfEntryDto>> Create([FromBody] SaveBillOfEntryCommand command, CancellationToken cancellationToken)
    {
        try { return Ok(await _service.CreateAsync(command, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{boeId:int}")]
    public async Task<ActionResult<BillOfEntryDto>> Update(int boeId, [FromBody] SaveBillOfEntryCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _service.UpdateAsync(boeId, command, cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("{boeId:int}")]
    public async Task<IActionResult> Delete(int boeId, CancellationToken cancellationToken)
    {
        try { return await _service.DeleteAsync(boeId, cancellationToken) ? NoContent() : NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
