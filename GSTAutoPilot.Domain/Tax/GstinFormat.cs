using System.Text.RegularExpressions;

namespace GSTAutoPilot.Domain.Tax;

public static partial class GstinFormat
{
    [GeneratedRegex(@"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z]{1}[1-9A-Z]{1}Z[0-9A-Z]{1}$")]
    private static partial Regex GstinPattern();

    public static GstinFormatResult Validate(string? gstin)
    {
        if (string.IsNullOrWhiteSpace(gstin))
        {
            return new GstinFormatResult(false, "GSTIN is required.");
        }

        var trimmed = gstin.Trim().ToUpperInvariant();
        if (trimmed.Length != 15)
        {
            return new GstinFormatResult(false, $"GSTIN must be 15 characters; got {trimmed.Length}.");
        }

        if (!GstinPattern().IsMatch(trimmed))
        {
            return new GstinFormatResult(false, "GSTIN does not match the required pattern (NN PPPPPPPPPP E Z C).");
        }

        var stateCode = int.Parse(trimmed[..2]);
        if (stateCode < 1 || stateCode > 38)
        {
            return new GstinFormatResult(false, $"State code '{trimmed[..2]}' is outside the valid range 01-38.");
        }

        return new GstinFormatResult(true, null);
    }

    public static string? GetStateName(string twoDigitStateCode) =>
        StateCodes.TryGetValue(twoDigitStateCode, out var name) ? name : null;

    public static IReadOnlyDictionary<string, string> States => StateCodes;

    private static readonly Dictionary<string, string> StateCodes = new()
    {
        ["01"] = "Jammu and Kashmir",
        ["02"] = "Himachal Pradesh",
        ["03"] = "Punjab",
        ["04"] = "Chandigarh",
        ["05"] = "Uttarakhand",
        ["06"] = "Haryana",
        ["07"] = "Delhi",
        ["08"] = "Rajasthan",
        ["09"] = "Uttar Pradesh",
        ["10"] = "Bihar",
        ["11"] = "Sikkim",
        ["12"] = "Arunachal Pradesh",
        ["13"] = "Nagaland",
        ["14"] = "Manipur",
        ["15"] = "Mizoram",
        ["16"] = "Tripura",
        ["17"] = "Meghalaya",
        ["18"] = "Assam",
        ["19"] = "West Bengal",
        ["20"] = "Jharkhand",
        ["21"] = "Odisha",
        ["22"] = "Chhattisgarh",
        ["23"] = "Madhya Pradesh",
        ["24"] = "Gujarat",
        ["25"] = "Daman and Diu",
        ["26"] = "Dadra and Nagar Haveli",
        ["27"] = "Maharashtra",
        ["28"] = "Andhra Pradesh",
        ["29"] = "Karnataka",
        ["30"] = "Goa",
        ["31"] = "Lakshadweep",
        ["32"] = "Kerala",
        ["33"] = "Tamil Nadu",
        ["34"] = "Puducherry",
        ["35"] = "Andaman and Nicobar Islands",
        ["36"] = "Telangana",
        ["37"] = "Andhra Pradesh (New)",
        ["38"] = "Ladakh",
    };
}

public readonly record struct GstinFormatResult(bool IsValid, string? Error);
