using System.Text.Json.Serialization;

namespace GSTAutoPilot.Application.DTOs;

// Portal-uploadable GSTN return JSON models. Property names are PascalCase and
// serialized with the SnakeCaseLower naming policy, which yields the GSTN keys
// (Ctin->ctin, InvTyp->inv_typ, HsnSc->hsn_sc, ...). The few keys that don't
// follow that mapping ("from"/"to") use [JsonPropertyName].

// ---------- GSTR-1 ----------
public class Gstr1Json
{
    public string Gstin { get; set; } = string.Empty;
    public string Fp { get; set; } = string.Empty; // filing period MMYYYY
    // version / hash are intentionally NOT emitted: the WhiteBooks GSP computes
    // and injects them at retsave/retsubmit. Emitting our own placeholder would
    // ship a bogus checksum. Null => omitted (serializer ignores null).
    public string? Version { get; set; }
    public string? Hash { get; set; }
    public List<Gstr1B2bCtin>? B2b { get; set; }
    public List<Gstr1B2clPos>? B2cl { get; set; }
    public List<Gstr1B2cs>? B2cs { get; set; }
    public List<Gstr1CdnrCtin>? Cdnr { get; set; }
    public List<Gstr1Cdnur>? Cdnur { get; set; }
    public List<Gstr1ExpGroup>? Exp { get; set; }
    public Gstr1HsnBlock? Hsn { get; set; }
    public Gstr1DocIssue? DocIssue { get; set; }
}

public class Gstr1ItemDet
{
    public decimal Txval { get; set; }
    public decimal Rt { get; set; }
    public decimal Iamt { get; set; }
    public decimal Camt { get; set; }
    public decimal Samt { get; set; }
    public decimal Csamt { get; set; }
}

public class Gstr1Item
{
    public int Num { get; set; }
    public Gstr1ItemDet ItmDet { get; set; } = new();
}

public class Gstr1B2bCtin
{
    public string Ctin { get; set; } = string.Empty;
    public List<Gstr1B2bInv> Inv { get; set; } = new();
}

public class Gstr1B2bInv
{
    public string Inum { get; set; } = string.Empty;
    public string Idt { get; set; } = string.Empty; // dd-MM-yyyy
    public decimal Val { get; set; }
    public string Pos { get; set; } = string.Empty; // 2-digit state code
    public string Rchrg { get; set; } = "N";
    public string InvTyp { get; set; } = "R";
    public List<Gstr1Item> Itms { get; set; } = new();
}

public class Gstr1B2clPos
{
    public string Pos { get; set; } = string.Empty;
    public List<Gstr1B2clInv> Inv { get; set; } = new();
}

public class Gstr1B2clInv
{
    public string Inum { get; set; } = string.Empty;
    public string Idt { get; set; } = string.Empty;
    public decimal Val { get; set; }
    public List<Gstr1Item> Itms { get; set; } = new();
}

public class Gstr1B2cs
{
    public string SplyTy { get; set; } = "INTRA"; // INTER / INTRA
    public string Pos { get; set; } = string.Empty;
    public string Typ { get; set; } = "OE";
    public decimal Rt { get; set; }
    public decimal Txval { get; set; }
    public decimal Iamt { get; set; }
    public decimal Camt { get; set; }
    public decimal Samt { get; set; }
    public decimal Csamt { get; set; }
}

public class Gstr1CdnrCtin
{
    public string Ctin { get; set; } = string.Empty;
    public List<Gstr1CdnrNote> Nt { get; set; } = new();
}

public class Gstr1CdnrNote
{
    public string Ntty { get; set; } = "C"; // C = credit note, D = debit note
    public string NtNum { get; set; } = string.Empty;
    public string NtDt { get; set; } = string.Empty;
    public decimal Val { get; set; }
    public string Pos { get; set; } = string.Empty;
    public string Rchrg { get; set; } = "N";
    public string InvTyp { get; set; } = "R";
    public List<Gstr1Item> Itms { get; set; } = new();
}

// Credit/debit note to an UNREGISTERED party (flat list, no ctin). typ is
// B2CL (inter-state large unregistered) / EXPWP / EXPWOP.
public class Gstr1Cdnur
{
    public string Ntty { get; set; } = "C";
    public string NtNum { get; set; } = string.Empty;
    public string NtDt { get; set; } = string.Empty;
    public string Typ { get; set; } = "B2CL";
    public string Pos { get; set; } = string.Empty;
    public decimal Val { get; set; }
    public List<Gstr1Item> Itms { get; set; } = new();
}

public class Gstr1ExpGroup
{
    public string ExpTyp { get; set; } = "WPAY"; // WPAY / WOPAY
    public List<Gstr1ExpInv> Inv { get; set; } = new();
}

public class Gstr1ExpInv
{
    public string Inum { get; set; } = string.Empty;
    public string Idt { get; set; } = string.Empty;
    public decimal Val { get; set; }
    public List<Gstr1ExpItem> Itms { get; set; } = new();
}

public class Gstr1ExpItem
{
    public decimal Txval { get; set; }
    public decimal Rt { get; set; }
    public decimal Iamt { get; set; }
    public decimal Csamt { get; set; }
}

// GSTR-1 Table 12 (HSN summary). Split into B2B (registered recipient / export)
// and B2C (unregistered) sub-tables — mandatory from the May-2025 tax period
// (serialized as hsn_b2b / hsn_b2c).
public class Gstr1HsnBlock
{
    public List<Gstr1HsnData> HsnB2b { get; set; } = new();
    public List<Gstr1HsnData> HsnB2c { get; set; } = new();
}

public class Gstr1HsnData
{
    public int Num { get; set; }
    public string HsnSc { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public string Uqc { get; set; } = "OTH";
    public decimal Qty { get; set; }
    public decimal Txval { get; set; }
    public decimal Iamt { get; set; }
    public decimal Camt { get; set; }
    public decimal Samt { get; set; }
    public decimal Csamt { get; set; }
    public decimal Rt { get; set; }
}

public class Gstr1DocIssue
{
    public List<Gstr1DocDet> DocDet { get; set; } = new();
}

public class Gstr1DocDet
{
    public int DocNum { get; set; }
    public List<Gstr1DocRange> Docs { get; set; } = new();
}

public class Gstr1DocRange
{
    public int Num { get; set; }
    [JsonPropertyName("from")] public string From { get; set; } = string.Empty;
    [JsonPropertyName("to")] public string To { get; set; } = string.Empty;
    public int Totnum { get; set; }
    public int Cancel { get; set; }
    public int NetIssue { get; set; }
}

// ---------- GSTR-3B ----------
public class Gstr3bJson
{
    public string Gstin { get; set; } = string.Empty;
    public string RetPeriod { get; set; } = string.Empty; // MMYYYY
    public Gstr3bSupDetails SupDetails { get; set; } = new();
    public Gstr3bInterSup InterSup { get; set; } = new();
    public Gstr3bItcElg ItcElg { get; set; } = new();
    // Table 5 — exempt/nil-rated & non-GST inward supplies. Nullable so it is
    // omitted (WhenWritingNull) when there are none.
    public Gstr3bInwardSup? InwardSup { get; set; }
    // Table 5.1 — interest & late fee. Interest/late fee are a function of the
    // filing delay and net liability, known only at filing time — so this is
    // null (omitted) at build time and populated by the filing flow if late.
    public Gstr3bIntrLtfee? IntrLtfee { get; set; }
}

// Table 5 — values of exempt, nil-rated and non-GST INWARD supplies, each split
// inter-state vs intra-state. `ty` is "GST" (from a composition dealer / exempt
// / nil-rated supply) or "NONGST" (non-GST inward, e.g. petrol/alcohol).
public class Gstr3bInwardSup
{
    public List<Gstr3bInwardSupDetail> IsupDetails { get; set; } = new();
}

public class Gstr3bInwardSupDetail
{
    public string Ty { get; set; } = "GST"; // "GST" | "NONGST"
    public decimal Inter { get; set; }
    public decimal Intra { get; set; }
}

// Table 5.1 — interest and late fee, per tax head.
public class Gstr3bIntrLtfee
{
    public Gstr3bIntrDetails IntrDetails { get; set; } = new();
}

public class Gstr3bIntrDetails
{
    public decimal Iamt { get; set; }
    public decimal Camt { get; set; }
    public decimal Samt { get; set; }
    public decimal Csamt { get; set; }
}

// Table 3.2 — inter-state supplies to unregistered / composition / UIN, by POS.
public class Gstr3bInterSup
{
    public List<Gstr3bPosSupply> UnregDetails { get; set; } = new();
    public List<Gstr3bPosSupply> CompDetails { get; set; } = new();
    public List<Gstr3bPosSupply> UinDetails { get; set; } = new();
}

public class Gstr3bPosSupply
{
    public string Pos { get; set; } = string.Empty;
    public decimal Txval { get; set; }
    public decimal Iamt { get; set; }
}

public class Gstr3bSupDetails
{
    public Gstr3bTaxBlock OsupDet { get; set; } = new();      // 3.1(a)
    public Gstr3bZeroBlock OsupZero { get; set; } = new();    // 3.1(b)
    public Gstr3bNilBlock OsupNilExmp { get; set; } = new();  // 3.1(c)
    public Gstr3bTaxBlock IsupRev { get; set; } = new();      // 3.1(d)
    public Gstr3bNilBlock OsupNongst { get; set; } = new();   // 3.1(e)
}

public class Gstr3bTaxBlock
{
    public decimal Txval { get; set; }
    public decimal Iamt { get; set; }
    public decimal Camt { get; set; }
    public decimal Samt { get; set; }
    public decimal Csamt { get; set; }
}

public class Gstr3bZeroBlock
{
    public decimal Txval { get; set; }
    public decimal Iamt { get; set; }
    public decimal Csamt { get; set; }
}

public class Gstr3bNilBlock
{
    public decimal Txval { get; set; }
}

public class Gstr3bItcElg
{
    public List<Gstr3bItcRow> ItcAvl { get; set; } = new();   // 4(A)
    public List<Gstr3bItcRow> ItcRev { get; set; } = new();   // 4(B) reversals
    public Gstr3bItcNet ItcNet { get; set; } = new();         // 4(C) net = 4A - 4B
    public List<Gstr3bItcRow> ItcInelg { get; set; } = new(); // 4(D) ineligible
}

public class Gstr3bItcNet
{
    public decimal Iamt { get; set; }
    public decimal Camt { get; set; }
    public decimal Samt { get; set; }
    public decimal Csamt { get; set; }
}

public class Gstr3bItcRow
{
    public string Ty { get; set; } = "OTH"; // IMPG / IMPS / ISRC / ISD / OTH
    public decimal Iamt { get; set; }
    public decimal Camt { get; set; }
    public decimal Samt { get; set; }
    public decimal Csamt { get; set; }
}
