namespace GSTAutoPilot.Domain.Tax;

public static class GstNetPayableCalculator
{
    public static GstNetPayableResult Compute(
        decimal outputIGST, decimal outputCGST, decimal outputSGST,
        decimal itcIGST, decimal itcCGST, decimal itcSGST)
    {
        var liabIGST = outputIGST;
        var liabCGST = outputCGST;
        var liabSGST = outputSGST;

        var igstCredit = itcIGST;
        var cgstCredit = itcCGST;
        var sgstCredit = itcSGST;

        var use = Math.Min(igstCredit, liabIGST);
        liabIGST -= use;
        igstCredit -= use;

        use = Math.Min(igstCredit, liabCGST);
        liabCGST -= use;
        igstCredit -= use;

        use = Math.Min(igstCredit, liabSGST);
        liabSGST -= use;
        igstCredit -= use;

        use = Math.Min(cgstCredit, liabCGST);
        liabCGST -= use;
        cgstCredit -= use;

        use = Math.Min(sgstCredit, liabSGST);
        liabSGST -= use;
        sgstCredit -= use;

        return new GstNetPayableResult(
            NetIGST: liabIGST,
            NetCGST: liabCGST,
            NetSGST: liabSGST,
            CarryIGST: igstCredit,
            CarryCGST: cgstCredit,
            CarrySGST: sgstCredit);
    }
}

public readonly record struct GstNetPayableResult(
    decimal NetIGST,
    decimal NetCGST,
    decimal NetSGST,
    decimal CarryIGST,
    decimal CarryCGST,
    decimal CarrySGST);
