namespace GSTAutoPilot.Application.Exceptions;

// Guards the two ways a NIL filing can go wrong, both of which end with the
// wrong thing declared to the government:
//
//   ConfirmationRequired — the prepared return turned out to have no
//     transactions. That may be perfectly correct, but "nothing to declare" is
//     a statement in its own right and must be made deliberately, not reached by
//     clicking Lock on a period whose data simply failed to load.
//   NotNil — the user asked to file NIL, but the period does have transactions.
//     Filing NIL over real supplies understates turnover; refuse outright.
public sealed class NilReturnConfirmationException : Exception
{
    public const string ConfirmationRequired = "NIL_CONFIRMATION_REQUIRED";
    public const string NotNil = "NOT_A_NIL_RETURN";

    // One of the two constants above.
    public string Reason { get; }
    public string Period { get; }
    public string ReturnType { get; }

    public NilReturnConfirmationException(string reason, string returnType, string period, string message)
        : base(message)
    {
        Reason = reason;
        ReturnType = returnType;
        Period = period;
    }

    public static NilReturnConfirmationException NeedsConfirmation(string returnType, string period) =>
        new(ConfirmationRequired, returnType, period,
            $"{Label(returnType)} for {period} has no transactions to report. Filing it would declare a NIL return "
            + "for the period. Confirm that the period is genuinely empty before continuing.");

    public static NilReturnConfirmationException HasData(string returnType, string period) =>
        new(NotNil, returnType, period,
            $"{Label(returnType)} for {period} contains transactions, so it cannot be filed as a NIL return. "
            + "Lock it as a normal return instead.");

    private static string Label(string returnType)
        => returnType.Trim().ToLowerInvariant() == "gstr1" ? "GSTR-1" : "GSTR-3B";
}
