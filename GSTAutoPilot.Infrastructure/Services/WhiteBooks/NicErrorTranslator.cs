using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GSTAutoPilot.Infrastructure.Services.WhiteBooks;

/// <summary>
/// Turns the government portal's raw rejection into something a user can act on.
/// </summary>
/// <remarks>
/// NIC rejects with a JSON array of codes, which WhiteBooks passes through in
/// status_desc. Shown as-is it reaches the user as, for example:
/// <code>
/// IRN failed: WhiteBooks IRN generation rejected: [{"errorCode":"2305",
/// "errorMessage":"IRN cannot be generated with document date prior to 30 days
/// from today for suppliers with turnover above 500 Cr"}, ...]
/// </code>
/// which tells an accounts clerk nothing. Every message below was written from a
/// real rejection observed during testing on 8-9 September 2026.
/// </remarks>
public static class NicErrorTranslator
{
    private sealed record NicError(string? ErrorCode, string? ErrorMessage);

    /// <summary>Plain-English text for a rejection, or null if none can be made.</summary>
    public static string? ToPlainEnglish(string? raw)
    {
        var errors = Parse(raw);
        if (errors.Count == 0) return null;

        var lines = new List<string>();
        foreach (var e in errors)
        {
            var friendly = Explain(e.ErrorCode, e.ErrorMessage);
            if (!string.IsNullOrWhiteSpace(friendly)) lines.Add(friendly!);
        }
        if (lines.Count == 0) return null;

        // A bad counter-party GSTIN also stops the portal working out which state
        // the customer is in, so it reports a tax-type problem as well. Saying so
        // stops people chasing a tax fault that will disappear on its own.
        var hasGstinFault = errors.Any(e => e.ErrorCode is "3028" or "3029" or "3030");
        var hasTaxTypeFault = errors.Any(e => e.ErrorCode is "2174" or "2175");
        if (hasGstinFault && hasTaxTypeFault)
        {
            lines.Add("The tax-type message above is a knock-on from the GST number and should clear once that is corrected.");
        }

        var sb = new StringBuilder("The government did not accept this invoice. ");
        sb.Append(lines.Count == 1 ? lines[0] : string.Join(" ", lines.Select((l, i) => $"({i + 1}) {l}")));
        return sb.ToString();
    }

    private static List<NicError> Parse(string? raw)
    {
        var list = new List<NicError>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        // status_desc usually IS the array; sometimes it is wrapped in prose.
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<NicError>>(
                    raw[start..(end + 1)],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed is not null) list.AddRange(parsed.Where(p => p is not null));
            }
            catch (JsonException)
            {
                // Not the shape we expected — fall through to the regex below.
            }
        }

        if (list.Count == 0)
        {
            foreach (Match m in Regex.Matches(raw, @"""errorCode""\s*:\s*""(?<c>[^""]+)""\s*,\s*""errorMessage""\s*:\s*""(?<m>[^""]*)""", RegexOptions.IgnoreCase))
            {
                list.Add(new NicError(m.Groups["c"].Value, m.Groups["m"].Value));
            }
        }
        return list;
    }

    private static string? Explain(string? code, string? portalMessage)
    {
        var gstin = ExtractGstin(portalMessage);
        return code switch
        {
            "2305" => "This invoice is more than 30 days old, and the government only allows an e-invoice to be raised within 30 days of the invoice date.",
            "2150" => "This invoice already has an IRN — it cannot be registered a second time.",
            "3028" or "3029" or "3030" =>
                $"The customer's GST number{(gstin is null ? string.Empty : $" ({gstin})")} was not accepted by the government. It may have changed, been cancelled, or been mistyped. Please confirm it with the customer.",
            "2174" or "2175" => "The type of tax on the invoice does not match the customer's state.",
            "2276" => "Your own PIN code is missing from the company details.",
            "2274" => "The customer's PIN code is missing from their address.",
            "2189" => "The invoice total does not match the taxable value plus tax. If a discount was given, it needs to be shown separately on the invoice.",
            "2258" => "Your GST number and the state on your address do not agree.",
            "3039" => "The PIN code does not belong to the state on the GST number.",
            "1015" => "This GST number is not registered against the login being used.",
            "2212" => "The invoice number contains characters the government does not allow.",
            "2265" => "The customer's GST number and their state do not agree.",
            _ => string.IsNullOrWhiteSpace(portalMessage)
                ? null
                // Unknown code: pass the portal's own wording through, tidied, so
                // nothing is ever silently swallowed.
                : $"{portalMessage!.Trim().TrimEnd('.')}.",
        };
    }

    private static string? ExtractGstin(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var m = Regex.Match(message, @"\b[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][0-9A-Z]Z[0-9A-Z]\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }
}
