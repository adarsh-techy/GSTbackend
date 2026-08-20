namespace GSTAutoPilot.Application.DTOs;

// Answers "would filing this period be a NIL return?" — computed from the
// prepared payload, so the UI can offer NIL filing as a deliberate choice
// rather than the user meeting a confirmation prompt after pressing Lock.
public class NilCheckResponse
{
    public string Period { get; set; } = string.Empty;
    public FilingType Type { get; set; }

    // True when the prepared return declares no transactions at all.
    public bool IsNil { get; set; }

    // Book figures behind the verdict, so the user can see WHY it is nil (or
    // why it is not) before deciding.
    public int InvoiceCount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Tax { get; set; }

    // Plain-language explanation for the confirmation dialog.
    public string Reason { get; set; } = string.Empty;
}
