using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

// Builds portal-uploadable GSTN-schema return JSON (GSTR-1, GSTR-3B).
public interface IGstnReturnService
{
    Task<Gstr1Json> BuildGstr1Async(int year, int month, CancellationToken cancellationToken = default);
    Task<Gstr3bJson> BuildGstr3bAsync(int year, int month, CancellationToken cancellationToken = default);
    // Serialize a GSTN model with the GSTN naming/format conventions.
    string Serialize(object gstnModel);
}
