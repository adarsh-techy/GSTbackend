using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface ITenantSettingsService
{
    Task<TenantSettingsDto> GetAsync(CancellationToken cancellationToken = default);
    Task<TenantSettingsDto> UpdateAsync(TenantSettingsDto dto, CancellationToken cancellationToken = default);
    Task<ErpProfileDto> GetErpProfileAsync(CancellationToken cancellationToken = default);
    Task<ErpProfileDto> UpdateErpProfileAsync(ErpProfileDto dto, CancellationToken cancellationToken = default);
    Task<SpProfileDto> GetSpProfileAsync(CancellationToken cancellationToken = default);
    Task<SpProfileDto> UpdateSpProfileAsync(SpProfileDto dto, CancellationToken cancellationToken = default);
    Task<WhiteBooksStatusDto> GetWhiteBooksAsync(CancellationToken cancellationToken = default);
    Task<WhiteBooksStatusDto> SaveWhiteBooksAsync(WhiteBooksConfigCommand cmd, CancellationToken cancellationToken = default);
    Task DisableWhiteBooksAsync(CancellationToken cancellationToken = default);
    // Toggle Sandbox-vs-Production for the tenant without touching credentials.
    Task<WhiteBooksStatusDto> SetWhiteBooksEnvironmentAsync(bool useSandbox, CancellationToken cancellationToken = default);
    // Read-only view of the shared sandbox account from appsettings.
    WhiteBooksSandboxInfoDto GetWhiteBooksSandboxInfo();

    // WhiteBooks GST API config (separate from e-Invoice).
    Task<WhiteBooksGstStatusDto> GetGstApiAsync(CancellationToken cancellationToken = default);
    Task<WhiteBooksGstStatusDto> SaveGstApiAsync(WhiteBooksGstConfigCommand cmd, CancellationToken cancellationToken = default);
    Task DisableGstApiAsync(CancellationToken cancellationToken = default);

    // SMTP / email config.
    Task<SmtpStatusDto> GetSmtpAsync(CancellationToken cancellationToken = default);
    Task<SmtpStatusDto> SaveSmtpAsync(SmtpConfigCommand cmd, CancellationToken cancellationToken = default);
    // Build an SMTP config from a command (for "send test" before saving),
    // falling back to the stored password when the command omits it.
    Task<SmtpConfig> ResolveSmtpAsync(SmtpConfigCommand cmd, CancellationToken cancellationToken = default);
    // The stored, decrypted SMTP config for the current tenant (for sending).
    Task<SmtpConfig> GetSmtpConfigAsync(CancellationToken cancellationToken = default);
}
