using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GSTAutoPilot.API.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private static readonly string[] AllowedLogoContentTypes = { "image/png", "image/jpeg", "image/jpg" };
    private const long MaxLogoBytes = 2 * 1024 * 1024;

    private readonly ITenantSettingsService _service;
    private readonly IDocumentMappingService _mappingService;
    private readonly IWebHostEnvironment _hostEnv;
    private readonly GSTAutoPilot.Infrastructure.Services.WhiteBooks.IWhiteBooksClient _whiteBooks;
    private readonly GSTAutoPilot.Infrastructure.Services.WhiteBooksGst.IWhiteBooksGstClient _gst;
    private readonly IEmailService _email;

    public SettingsController(
        ITenantSettingsService service,
        IDocumentMappingService mappingService,
        IWebHostEnvironment hostEnv,
        GSTAutoPilot.Infrastructure.Services.WhiteBooks.IWhiteBooksClient whiteBooks,
        GSTAutoPilot.Infrastructure.Services.WhiteBooksGst.IWhiteBooksGstClient gst,
        IEmailService email)
    {
        _service = service;
        _mappingService = mappingService;
        _hostEnv = hostEnv;
        _whiteBooks = whiteBooks;
        _gst = gst;
        _email = email;
    }

    [HttpGet("document-mappings")]
    public async Task<ActionResult<IReadOnlyList<DocumentMappingDto>>> GetDocumentMappings(CancellationToken cancellationToken)
    {
        try { return Ok(await _mappingService.GetMappingsAsync(cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("document-mappings")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IReadOnlyList<DocumentMappingDto>>> UpdateDocumentMappings(
        [FromBody] UpdateDocumentMappingsCommand command,
        CancellationToken cancellationToken)
    {
        try { return Ok(await _mappingService.UpdateMappingsAsync(command, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("discover-doctypes")]
    public async Task<ActionResult<DocTypeDiscoveryResponse>> DiscoverDocTypes(
        [FromQuery] string? headerTable,
        CancellationToken cancellationToken)
    {
        try { return Ok(await _mappingService.DiscoverDocTypesAsync(headerTable, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("known-line-tables")]
    public async Task<ActionResult<KnownTablesResponse>> GetKnownTables(CancellationToken cancellationToken)
    {
        try { return Ok(await _mappingService.GetKnownTablesAsync(cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("whitebooks")]
    public async Task<ActionResult<WhiteBooksStatusDto>> GetWhiteBooks(CancellationToken cancellationToken)
    {
        try { return Ok(await _service.GetWhiteBooksAsync(cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("whitebooks")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WhiteBooksStatusDto>> SaveWhiteBooks(
        [FromBody] WhiteBooksConfigCommand cmd,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate the credentials with a live auth call before persisting.
            await _whiteBooks.TestConnectionAsync(cmd.ClientId, cmd.ClientSecret, cmd.UseSandbox, cmd.Username, cmd.Password, cancellationToken);
            return Ok(await _service.SaveWhiteBooksAsync(cmd, cancellationToken));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return BadRequest(new { error = $"Could not reach WhiteBooks: {ex.Message}" }); }
    }

    [HttpDelete("whitebooks")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DisableWhiteBooks(CancellationToken cancellationToken)
    {
        try { await _service.DisableWhiteBooksAsync(cancellationToken); return NoContent(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("whitebooks/sandbox")]
    public ActionResult<WhiteBooksSandboxInfoDto> GetWhiteBooksSandboxInfo()
    {
        return Ok(_service.GetWhiteBooksSandboxInfo());
    }

    [HttpPost("whitebooks/sandbox/test")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> TestWhiteBooksSandbox(CancellationToken cancellationToken)
    {
        try
        {
            // Sandbox creds are the shared BVMGSP defaults — no user input.
            await _whiteBooks.TestConnectionAsync(string.Empty, string.Empty, useSandbox: true, null, null, cancellationToken);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return BadRequest(new { error = $"Could not reach WhiteBooks: {ex.Message}" }); }
    }

    [HttpPut("whitebooks/environment")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WhiteBooksStatusDto>> SetWhiteBooksEnvironment(
        [FromBody] WhiteBooksEnvironmentCommand cmd,
        CancellationToken cancellationToken)
    {
        try { return Ok(await _service.SetWhiteBooksEnvironmentAsync(cmd.UseSandbox, cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("gst-api")]
    public async Task<ActionResult<WhiteBooksGstStatusDto>> GetGstApi(CancellationToken cancellationToken)
    {
        try { return Ok(await _service.GetGstApiAsync(cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("gst-api")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WhiteBooksGstStatusDto>> SaveGstApi([FromBody] WhiteBooksGstConfigCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            // Validate the credentials with a live auth call before persisting.
            await _gst.TestConnectionAsync(cmd.ClientId, cmd.ClientSecret, cancellationToken);
            return Ok(await _service.SaveGstApiAsync(cmd, cancellationToken));
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return BadRequest(new { error = $"Could not reach WhiteBooks GST API: {ex.Message}" }); }
    }

    [HttpDelete("gst-api")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DisableGstApi(CancellationToken cancellationToken)
    {
        try { await _service.DisableGstApiAsync(cancellationToken); return NoContent(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("email")]
    public async Task<ActionResult<SmtpStatusDto>> GetEmail(CancellationToken cancellationToken)
    {
        try { return Ok(await _service.GetSmtpAsync(cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("email")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SmtpStatusDto>> SaveEmail([FromBody] SmtpConfigCommand cmd, CancellationToken cancellationToken)
    {
        try { return Ok(await _service.SaveSmtpAsync(cmd, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("email/test")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> TestEmail([FromBody] SmtpConfigCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var config = await _service.ResolveSmtpAsync(cmd, cancellationToken);
            var to = config.Username.Contains('@') ? config.Username : config.FromEmail;
            var body = "This is a test email from GSTAutoPilot.\n\n"
                + $"SMTP host: {config.Host}:{config.Port}\nFrom: {config.FromEmail}\n\n"
                + "If you received this, your e-Invoice email configuration works.";
            await _email.SendAsync(config, new EmailMessage(to, null, "GSTAutoPilot SMTP test", body, Array.Empty<EmailAttachment>()), cancellationToken);
            return Ok(new { sentTo = to });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = $"Test email failed: {ex.Message}" }); }
    }

    [HttpGet]
    public async Task<ActionResult<TenantSettingsDto>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TenantSettingsDto>> Update(
        [FromBody] TenantSettingsDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.UpdateAsync(dto, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("erp-profile")]
    public async Task<ActionResult<ErpProfileDto>> GetErpProfile(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetErpProfileAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("erp-profile")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ErpProfileDto>> UpdateErpProfile(
        [FromBody] ErpProfileDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.UpdateErpProfileAsync(dto, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("sp-profile")]
    public async Task<ActionResult<SpProfileDto>> GetSpProfile(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.GetSpProfileAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("sp-profile")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SpProfileDto>> UpdateSpProfile(
        [FromBody] SpProfileDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.UpdateSpProfileAsync(dto, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("logo")]
    [Authorize(Roles = "Admin")]
    [RequestSizeLimit(MaxLogoBytes)]
    public async Task<ActionResult<TenantSettingsDto>> UploadLogo(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded." });
        }
        if (file.Length > MaxLogoBytes)
        {
            return BadRequest(new { error = $"Logo must be under {MaxLogoBytes / 1024} KB." });
        }
        if (!AllowedLogoContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Logo must be PNG or JPEG." });
        }
        if (HttpContext.Items["Tenant"] is not Tenant tenant)
        {
            return BadRequest(new { error = "Tenant not resolved." });
        }

        var webRoot = _hostEnv.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(_hostEnv.ContentRootPath, "wwwroot");
        }
        var dir = Path.Combine(webRoot, "uploads", "logos");
        Directory.CreateDirectory(dir);
        var ext = file.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var fileName = $"{tenant.TenantId}{ext}";
        var diskPath = Path.Combine(dir, fileName);
        await using (var fs = System.IO.File.Create(diskPath))
        {
            await file.CopyToAsync(fs, cancellationToken);
        }

        var relativePath = Path.Combine("uploads", "logos", fileName).Replace('\\', '/');
        var current = await _service.GetAsync(cancellationToken);
        current.LogoPath = relativePath;
        var updated = await _service.UpdateAsync(current, cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("logo")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TenantSettingsDto>> RemoveLogo(CancellationToken cancellationToken)
    {
        var current = await _service.GetAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(current.LogoPath))
        {
            var webRoot = _hostEnv.WebRootPath ?? Path.Combine(_hostEnv.ContentRootPath, "wwwroot");
            var diskPath = Path.IsPathRooted(current.LogoPath)
                ? current.LogoPath
                : Path.Combine(webRoot, current.LogoPath);
            try
            {
                if (System.IO.File.Exists(diskPath)) System.IO.File.Delete(diskPath);
            }
            catch (IOException)
            {
                // Best-effort: clearing the path is what actually disables the
                // logo on the PDF. A leftover orphan file is harmless.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        current.LogoPath = null;
        var updated = await _service.UpdateAsync(current, cancellationToken);
        return Ok(updated);
    }
}
