namespace GSTAutoPilot.Application.Services;

// Encrypts/decrypts small secrets (e.g. the SMTP password) at rest using the
// platform key ring, so they're never stored in plaintext in the database.
public interface ISecretProtector
{
    string Protect(string plaintext);
    bool TryUnprotect(string? protectedText, out string plaintext);
}
