using System.Text.Json;

namespace GSTAutoPilot.Infrastructure.Services;

// Decides whether a prepared return is NIL, by reading the payload we are about
// to file rather than by carrying a flag alongside it. A flag can drift out of
// step with the JSON; the JSON is what GSTN receives, so it is the only honest
// source of truth for "this return declares no transactions".
internal static class NilReturnDetector
{
    // GSTR-1 sections that carry supplies. hsn/doc_issue are deliberately
    // included: an HSN summary or a document-issued range with content means the
    // period had activity, so it is not a NIL return even if the invoice tables
    // somehow came out empty.
    private static readonly string[] Gstr1Sections =
    {
        "b2b", "b2cl", "b2cs", "cdnr", "cdnur", "exp", "nil", "at", "txpd", "hsn", "doc_issue",
    };

    public static bool IsNil(string returnType, string? payloadJson)
        => returnType.Trim().ToLowerInvariant() == "gstr1"
            ? IsNilGstr1(payloadJson)
            : IsNilGstr3b(payloadJson);

    // NIL GSTR-1: gstin + fp and nothing else with content in it.
    public static bool IsNilGstr1(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            foreach (var section in Gstr1Sections)
            {
                if (root.TryGetProperty(section, out var el) && HasContent(el)) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            // Unreadable payload: never claim NIL on a guess.
            return false;
        }
    }

    // NIL GSTR-3B: every numeric field in the return is zero. Walks the whole
    // document rather than naming tables, so a schema addition can't quietly
    // make a non-nil return look nil.
    public static bool IsNilGstr3b(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return AllNumbersZero(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasContent(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Array => el.GetArrayLength() > 0,
        JsonValueKind.Object => el.EnumerateObject().Any(p => HasContent(p.Value)),
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(el.GetString()),
        JsonValueKind.Number => el.TryGetDecimal(out var d) && d != 0m,
        JsonValueKind.False => false,
        _ => true,
    };

    private static bool AllNumbersZero(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetDecimal(out var d) && d == 0m;
            case JsonValueKind.Array:
                return el.EnumerateArray().All(AllNumbersZero);
            case JsonValueKind.Object:
                return el.EnumerateObject().All(p => AllNumbersZero(p.Value));
            default:
                // gstin, ret_period and the like carry no money.
                return true;
        }
    }
}
