using QRCoder;

namespace GSTAutoPilot.Infrastructure.Services;

// Turns a stored NIC SignedQRCode into PNG bytes. The value is either a
// base64-encoded image (decode + return) or a JWS/string payload (encode into
// a QR with QRCoder). Mirrors the logic in InvoicePdfService.
internal static class QrPngRenderer
{
    public static byte[]? ToPng(string? signedQrCode)
    {
        if (string.IsNullOrWhiteSpace(signedQrCode)) return null;
        var content = signedQrCode.Trim();

        if (TryDecodeBase64Image(content, out var imageBytes))
        {
            return imageBytes;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        return new PngByteQRCode(data).GetGraphic(8);
    }

    private static bool TryDecodeBase64Image(string content, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            var decoded = Convert.FromBase64String(content);
            // PNG (89 50) or JPEG (FF D8) magic bytes => it's already an image.
            if (decoded.Length > 4 &&
                ((decoded[0] == 0x89 && decoded[1] == 0x50) || (decoded[0] == 0xFF && decoded[1] == 0xD8)))
            {
                bytes = decoded;
                return true;
            }
        }
        catch (FormatException)
        {
            // Not base64 — caller falls back to encoding the string into a QR.
        }
        return false;
    }
}
