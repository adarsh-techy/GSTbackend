using System.Text.Json.Serialization;

namespace GSTAutoPilot.Infrastructure.Services.WhiteBooks;

// WhiteBooks (GSP) wraps every response in a status envelope:
//   { "status_cd": "1", "status_desc": "...", "data": {...}, "header": {...} }
// status_cd "1" (or "Sucess" — their typo) = success. The `header` echoes the
// request headers WhiteBooks captured AND, critically, includes a `txn` that
// is the session reference NIC requires on subsequent /einvoice calls. Missing
// the txn on the generate call → NIC error 1005 "Invalid Token".
public class WhiteBooksEnvelope<T>
{
    [JsonPropertyName("status_cd")] public string? StatusCd { get; set; }
    [JsonPropertyName("status_desc")] public string? StatusDesc { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("header")] public WhiteBooksHeader? Header { get; set; }

    public bool IsSuccess => StatusCd == "1" || string.Equals(StatusCd, "Sucess", StringComparison.OrdinalIgnoreCase);
}

public class WhiteBooksHeader
{
    [JsonPropertyName("txn")] public string? Txn { get; set; }
}

public class WhiteBooksAuthData
{
    [JsonPropertyName("AuthToken")] public string? AuthToken { get; set; }
    [JsonPropertyName("Sek")] public string? Sek { get; set; }
    // TokenExpiry is returned as a string timestamp like "2026-05-21 18:30:00".
    [JsonPropertyName("TokenExpiry")] public string? TokenExpiry { get; set; }
}

public class WhiteBooksIrnData
{
    [JsonPropertyName("Irn")] public string? Irn { get; set; }
    [JsonPropertyName("AckNo")] public long? AckNo { get; set; }
    [JsonPropertyName("AckDt")] public string? AckDt { get; set; }
    [JsonPropertyName("SignedInvoice")] public string? SignedInvoice { get; set; }
    [JsonPropertyName("SignedQRCode")] public string? SignedQRCode { get; set; }
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("EwbNo")] public string? EwbNo { get; set; }
}

// The IRN result surfaced to the rest of the app (provider-agnostic).
public record EInvoiceProviderResult(
    string Irn,
    string AckNo,
    DateTime AckDate,
    string SignedInvoice,
    string SignedQrCode,
    string Status);
