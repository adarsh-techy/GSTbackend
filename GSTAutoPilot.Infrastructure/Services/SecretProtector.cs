using GSTAutoPilot.Application.Services;
using Microsoft.AspNetCore.DataProtection;

namespace GSTAutoPilot.Infrastructure.Services;

public class SecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("GSTAutoPilot.TenantSecrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public bool TryUnprotect(string? protectedText, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrEmpty(protectedText)) return false;
        try
        {
            plaintext = _protector.Unprotect(protectedText);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Key rotated / value not actually protected — treat as no secret.
            return false;
        }
    }
}
