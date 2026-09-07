using GSTAutoPilot.Application.DTOs;

namespace GSTAutoPilot.Application.Services;

public interface IImsService
{
    // Live fetch from GSTN via GSP (WhiteBooks) session into memory.
    Task<ImsInwardFetchResponse> FetchInwardAsync(string filingPeriod, CancellationToken cancellationToken = default);

    // In-memory parse of official GST portal downloaded IMS JSON string / stream.
    ImsInwardFetchResponse ParseImsJson(string jsonContent, string filingPeriod);

    // Relays action submissions (Accept/Reject/Pending/Reset) to GSTN via GSP.
    Task<ImsActionSubmitResponse> SubmitActionsAsync(ImsActionSubmitRequest request, CancellationToken cancellationToken = default);

    // Generates official GST portal compatible action JSON file content for manual upload to gst.gov.in.
    string GenerateActionJson(ImsActionSubmitRequest request);
}
