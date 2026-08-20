using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IFilingService
{
    // `confirmNil` is the caller's explicit acknowledgement that a period with
    // no transactions is being locked as a NIL return. Locking an empty period
    // without it throws NilReturnConfirmationException; passing it for a period
    // that DOES have transactions throws too.
    Task<FilingResponse> LockGstr1Async(string period, bool confirmNil = false, CancellationToken cancellationToken = default);
    Task<FilingResponse> LockGstr3bAsync(string period, bool confirmNil = false, CancellationToken cancellationToken = default);
    // Whether the prepared return for this period would be a NIL filing, so the
    // UI can offer it deliberately instead of discovering it at lock time.
    Task<NilCheckResponse> CheckNilAsync(string period, FilingType type, CancellationToken cancellationToken = default);
    Task<FilingResponse> MarkFiledAsync(Guid filingId, MarkFiledCommand command, CancellationToken cancellationToken = default);
    // Direct GSTN filing (via GSP): retsave the locked return, then retsubmit to
    // lock it on the portal. Stops at SaveFailed if GSTN rejects rows on save.
    Task<GstnSubmitResponse> SubmitToGstnAsync(Guid filingId, CancellationToken cancellationToken = default);
    // File the submitted return with a fresh OTP; captures the ARN and marks Filed.
    Task<FilingResponse> FileWithEvcAsync(Guid filingId, FileWithEvcCommand command, CancellationToken cancellationToken = default);
    // GSTN's own computed summary (retsum) for the period, as raw JSON, for
    // comparison against our figures before filing. type is "gstr1"/"gstr3b".
    Task<string> GetGstnSummaryAsync(string type, string period, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FilingResponse>> ListAsync(string? period, FilingType? type, CancellationToken cancellationToken = default);
    Task<FilingDetailResponse?> GetAsync(Guid filingId, CancellationToken cancellationToken = default);
    Task<FilingResponse?> LatestAsync(string period, FilingType type, CancellationToken cancellationToken = default);
}
