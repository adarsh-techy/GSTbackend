namespace GSTAutoPilot.Application.Exceptions;

// Thrown when a GSTN operation (e.g. fetching GSTR-2B) is attempted without the
// prerequisites: the GST API isn't configured for the tenant, or there's no
// live OTP session. Distinct from other failures so the API can return a status
// the UI recognises and respond by prompting the user to connect / authenticate
// — never by fabricating data.
public sealed class GstnNotConnectedException : Exception
{
    public const string NotConfigured = "not_configured";
    public const string NoSession = "no_session";

    // "not_configured" | "no_session"
    public string Reason { get; }

    public GstnNotConnectedException(string reason, string message) : base(message)
    {
        Reason = reason;
    }
}
