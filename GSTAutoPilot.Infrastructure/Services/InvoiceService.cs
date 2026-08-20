using GSTAutoPilot.Application.Configuration;
using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Domain.Entities;
using GSTAutoPilot.Infrastructure.CarolERP;
using GSTAutoPilot.Infrastructure.CarolERP.Entities;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class InvoiceService : IInvoiceService
{
    // e-Invoicing is mandatory above this per-invoice value (current GST rule
    // is turnover-based, but we flag per-invoice >5L as "Required" for the UI).
    private const decimal EInvoiceThreshold = 500_000m;

    private readonly CarolDocumentReader _reader;
    private readonly CarolERPDbContext _carol;
    private readonly Persistence.TenantDbContext _db;
    private readonly SpOutwardService _spOutward;

    public InvoiceService(CarolDocumentReader reader, CarolERPDbContext carol, Persistence.TenantDbContext db, SpOutwardService spOutward)
    {
        _reader = reader;
        _carol = carol;
        _db = db;
        _spOutward = spOutward;
    }

    public async Task<IReadOnlyList<InvoiceResponse>> ListAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        // When the tenant is configured with an outward SP, that is the sole data
        // source (the SP owns all GST logic); the table-mapping path below is only
        // used for tenants without an SP.
        if (_spOutward.IsConfigured)
        {
            var spInvoices = await _spOutward.ListAsync(year, month, cancellationToken);
            // A tenant SP may classify little or nothing (KSCC's hardcodes 'B2B'
            // on every row), so the section is normalised here for every SP
            // tenant: unregistered buyers out of B2B first, then the B2CL split.
            NormalizeUnregisteredSections(spInvoices);
            NormalizeB2clB2cs(spInvoices);
            return spInvoices;
        }

        var bundles = (await _reader.ReadOutwardAsync(year, month, cancellationToken))
            .OrderByDescending(b => b.Header.BillDate)
            .ToList();
        var accounts = await FetchAccountsAsync(bundles, cancellationToken);
        var (docToCompany, companyNames) = await FetchCompanyMapsAsync(cancellationToken);
        var docToPrefix = await _carol.DocIdToPrefixMapAsync(cancellationToken);
        var roundOffs = await _reader.ReadRoundOffAsync(bundles.Select(b => b.Header.BillId).ToList(), cancellationToken);

        // When several bills share the same FULL number (prefix + BillNumber)
        // and have no InvNo, append the BillId so the user can still tell the
        // rows apart. The prefix is part of the key — CC/17 and CCR/17 are
        // distinct series, so they must NOT be treated as duplicates (that
        // wrongly leaked the internal BillId into otherwise-unique numbers).
        var duplicateNumbers = bundles
            .Where(b => string.IsNullOrWhiteSpace(b.Header.InvNo))
            .GroupBy(b => FullNumberKey(b.Header, docToPrefix))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var invoices = bundles
            .Select(b => MapToInvoiceResponse(b.Header, Lookup(accounts, b.Header.AccountId), b.Lines, duplicateNumbers, docToCompany, companyNames, docToPrefix,
                roundOffs.TryGetValue(b.Header.BillId, out var ro) ? ro : null))
            .ToList();
        await ApplyEInvoiceStatusAsync(invoices, cancellationToken);
        NormalizeB2clB2cs(invoices);
        return invoices;
    }

    // "B2B" without a valid counter-party GSTIN is a contradiction: B2B means a
    // registered recipient. It happens when the data source labels rows B2B
    // wholesale (KSCC's outward SP hardcodes GstType = 'B2B'), and it used to be
    // fatal — GstnReturnService routes B2B on `Section == "B2B" && IsGstin(...)`,
    // so these invoices matched no table at all and left the return silently
    // short (52 invoices / Rs 97.9L taxable in KSCC's Oct-2025 book).
    //
    // Such an invoice is a supply to an unregistered person: move it to the B2C
    // space and let NormalizeB2clB2cs pick B2CL vs B2CS. Exports are already
    // marked Export by the reader / SpOutwardService and are left alone, as are
    // credit-debit notes (CDN routes on GSTIN validity by itself).
    private static void NormalizeUnregisteredSections(IReadOnlyList<InvoiceResponse> invoices)
    {
        foreach (var inv in invoices)
        {
            if (!string.Equals(inv.Section, "B2B", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsValidGstin(inv.PartyGSTIN)) continue;
            inv.Section = "B2CS";
        }
    }

    // B2CL vs B2CS is a pure function of (inter-state?, invoice value, statutory
    // threshold), so normalise it in the app for EVERY data source rather than
    // trusting each tenant's SP to get it right (KSCC's outward SP, for one,
    // never emits B2CL). Only rows already in the unregistered-B2C space are
    // touched; B2B / Export / CDN are left as classified. Inter-state is
    // inferred from IGST being present on the invoice.
    private static void NormalizeB2clB2cs(IReadOnlyList<InvoiceResponse> invoices)
    {
        foreach (var inv in invoices)
        {
            if (inv.Section is not ("B2CL" or "B2CS")) continue;
            var interState = inv.IGST != 0m;
            inv.Section = interState && inv.TotalAmount > B2clThresholdFor(inv.InvoiceDate)
                ? "B2CL" : "B2CS";
        }
    }

    // Returns (DocId → CoId map, CoId → CompanyName map) so each invoice row
    // can be tagged with the company it belongs to. Bill_Mas / Bill_File_mas
    // don't carry CoId; the bill's company is resolved through Documents.CoId.
    private async Task<(IReadOnlyDictionary<short, byte> DocToCompany, IReadOnlyDictionary<byte, string> Names)> FetchCompanyMapsAsync(CancellationToken ct)
    {
        var docToCompany = await _carol.DocIdToCompanyMapAsync(ct);
        // Project only the columns we need — loading the full CarolCompany
        // row triggers SELECT StateId etc., which casts as int and breaks on
        // installs where StateId is tinyint. See CarolErpFlavor notes.
        var pairs = await _carol.Companies.AsNoTracking()
            .Select(c => new { c.CoId, c.CoName })
            .ToListAsync(ct);
        var names = pairs.ToDictionary(p => p.CoId, p => p.CoName ?? string.Empty);
        return (docToCompany, names);
    }

    public async Task<IReadOnlyList<Gstr1SummaryRow>> GetGstr1SummaryAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        if (_spOutward.IsConfigured)
            return SummaryFromInvoices(await ListAsync(year, month, cancellationToken));

        var bundles = await _reader.ReadOutwardAsync(year, month, cancellationToken);
        var accounts = await FetchAccountsAsync(bundles, cancellationToken);

        // The party summary covers actual supplies (B2B / Export / B2C). Credit
        // and debit notes are reported separately, so they're excluded here.
        return bundles
            .Where(b => !IsCreditDebitNote(b.Header.GstCategory))
            .GroupBy(b => b.Header.AccountId)
            .Select(g =>
            {
                var account = Lookup(accounts, g.Key);
                var representative = g.First().Header;
                decimal taxable = 0m, igst = 0m, cgst = 0m, sgst = 0m, total = 0m;
                foreach (var b in g)
                {
                    var f = ComputeFigures(b.Header, b.Lines);
                    taxable += f.Taxable;
                    igst += f.Igst;
                    cgst += f.Cgst;
                    sgst += f.Sgst;
                    total += f.Total;
                }
                var isExport = g.Any(b => b.Header.GstCategory == GstDocumentCatalog.ExportSales)
                    || PartyGstinLabel(representative, account) == "Export";
                var section = isExport
                    ? "Export"
                    : HasRealGstin(representative, account) ? "B2B"
                    : (igst > 0m && total > B2clThresholdFor(representative.BillDate) ? "B2CL" : "B2CS");
                return new Gstr1SummaryRow
                {
                    PartyName = account?.AccountName ?? string.Empty,
                    PartyGSTIN = PartyGstinLabel(representative, account),
                    Section = section,
                    InvoiceCount = g.Count(),
                    TaxableValue = decimal.Round(taxable, 2),
                    CGST = decimal.Round(cgst, 2),
                    SGST = decimal.Round(sgst, 2),
                    IGST = decimal.Round(igst, 2),
                    TotalAmount = decimal.Round(total, 2),
                };
            })
            .OrderBy(r => r.PartyName)
            .ToList();
    }

    // GSTR-1 Table 12 (HSN summary) + Table 13 (documents issued), computed from
    // the outward bundles. Credit notes net down (sign), debit notes add.
    public async Task<Gstr1TablesResponse> GetGstr1TablesAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        if (_spOutward.IsConfigured)
            return TablesFromInvoices(await ListAsync(year, month, cancellationToken));

        var bundles = await _reader.ReadOutwardAsync(year, month, cancellationToken);
        var hsn = new Dictionary<(string SupplyType, string Hsn, decimal Rate), Gstr1HsnRow>();
        int invoices = 0, creditNotes = 0, debitNotes = 0;

        foreach (var b in bundles)
        {
            var cat = b.Header.GstCategory;
            var sign = GstDocumentCatalog.ReducesOutputTax(cat) ? -1m : 1m;
            if (cat == GstDocumentCatalog.CreditNote) creditNotes++;
            else if (cat == GstDocumentCatalog.SalesDebitNote) debitNotes++;
            else invoices++;
            // Table 12 B2B (registered recipient / export) vs B2C (unregistered).
            var supplyType = cat == GstDocumentCatalog.ExportSales || IsValidGstin(b.Header.GstNo) ? "B2B" : "B2C";

            foreach (var l in b.Lines)
            {
                var code = NormalizeHsn(l.Hsn);
                // Combined rate, derived from amounts when the line rate is 0
                // (intra-state / credit-note lines carry no IGST rate).
                var rate = GstRateHelper.Effective(l.IgstRate, l.TaxableInr, l.IgstAmount, l.CgstAmount, l.SgstAmount);
                var key = (supplyType, code, rate);
                if (!hsn.TryGetValue(key, out var row))
                {
                    row = new Gstr1HsnRow { HSNCode = code, Description = l.Description, Rate = rate, SupplyType = supplyType };
                    hsn[key] = row;
                }
                row.Quantity += sign * l.Quantity;
                row.TaxableValue += sign * l.TaxableInr;
                row.IGST += sign * l.IgstAmount;
                row.CGST += sign * l.CgstAmount;
                row.SGST += sign * l.SgstAmount;
            }
        }

        foreach (var row in hsn.Values)
        {
            row.Quantity = decimal.Round(row.Quantity, 3);
            row.TaxableValue = decimal.Round(row.TaxableValue, 2);
            row.IGST = decimal.Round(row.IGST, 2);
            row.CGST = decimal.Round(row.CGST, 2);
            row.SGST = decimal.Round(row.SGST, 2);
            row.TotalValue = decimal.Round(row.TaxableValue + row.IGST + row.CGST + row.SGST, 2);
        }

        return new Gstr1TablesResponse
        {
            Hsn = hsn.Values.OrderByDescending(r => r.TaxableValue).ToList(),
            DocsIssued = new List<Gstr1DocRow>
            {
                new() { DocType = "Invoices for outward supply", Count = invoices },
                new() { DocType = "Debit Notes", Count = debitNotes },
                new() { DocType = "Credit Notes", Count = creditNotes },
            },
        };
    }

    // GSTR-1 party-wise summary derived from the SP invoice list. The section is
    // whatever the SP classified (GstType); credit/debit notes (CDN) are reported
    // separately in the tables path, so they're excluded from this supply summary.
    private static IReadOnlyList<Gstr1SummaryRow> SummaryFromInvoices(IReadOnlyList<InvoiceResponse> invoices)
    {
        return invoices
            .Where(i => !string.Equals(i.Section, "CDN", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => new { i.PartyGSTIN, i.PartyName, i.Section })
            .Select(g => new Gstr1SummaryRow
            {
                PartyName = g.Key.PartyName ?? string.Empty,
                PartyGSTIN = g.Key.PartyGSTIN ?? string.Empty,
                Section = g.Key.Section,
                InvoiceCount = g.Count(),
                TaxableValue = decimal.Round(g.Sum(i => i.TaxableValue), 2),
                CGST = decimal.Round(g.Sum(i => i.CGST), 2),
                SGST = decimal.Round(g.Sum(i => i.SGST), 2),
                IGST = decimal.Round(g.Sum(i => i.IGST), 2),
                TotalAmount = decimal.Round(g.Sum(i => i.TotalAmount), 2),
            })
            .OrderBy(r => r.PartyName)
            .ToList();
    }

    // GSTR-1 Table 12 (HSN) + Table 13 (docs issued) from the SP invoice list. The
    // SP groups by rate line; CDN invoices net down. The SP contract carries no
    // line quantity/description, so those stay 0/blank in the HSN summary.
    private static Gstr1TablesResponse TablesFromInvoices(IReadOnlyList<InvoiceResponse> invoices)
    {
        var hsn = new Dictionary<(string SupplyType, string Hsn, decimal Rate), Gstr1HsnRow>();
        int invoiceCount = 0, creditNotes = 0;

        foreach (var i in invoices)
        {
            var isCdn = string.Equals(i.Section, "CDN", StringComparison.OrdinalIgnoreCase);
            var sign = isCdn ? -1m : 1m;
            if (isCdn) creditNotes++; else invoiceCount++;
            var supplyType = HsnSupplyType(i.Section, i.PartyGSTIN);

            foreach (var l in i.Lines)
            {
                var code = NormalizeHsn(l.HSNCode);
                var rate = l.GstRate;
                var key = (supplyType, code, rate);
                if (!hsn.TryGetValue(key, out var row))
                {
                    row = new Gstr1HsnRow { HSNCode = code, Description = l.Description ?? string.Empty, Rate = rate, SupplyType = supplyType };
                    hsn[key] = row;
                }
                row.Quantity += sign * l.Quantity;
                row.TaxableValue += sign * l.TaxableValue;
                row.IGST += sign * l.IGST;
                row.CGST += sign * l.CGST;
                row.SGST += sign * l.SGST;
                row.Cess += sign * l.Cess;
            }
        }

        foreach (var row in hsn.Values)
        {
            row.Quantity = decimal.Round(row.Quantity, 3);
            row.TaxableValue = decimal.Round(row.TaxableValue, 2);
            row.IGST = decimal.Round(row.IGST, 2);
            row.CGST = decimal.Round(row.CGST, 2);
            row.SGST = decimal.Round(row.SGST, 2);
            row.Cess = decimal.Round(row.Cess, 2);
            row.TotalValue = decimal.Round(row.TaxableValue + row.IGST + row.CGST + row.SGST + row.Cess, 2);
        }

        return new Gstr1TablesResponse
        {
            Hsn = hsn.Values.OrderByDescending(r => r.TaxableValue).ToList(),
            DocsIssued = new List<Gstr1DocRow>
            {
                new() { DocType = "Invoices for outward supply", Count = invoiceCount },
                new() { DocType = "Debit Notes", Count = 0 },
                new() { DocType = "Credit Notes", Count = creditNotes },
            },
        };
    }

    public async Task<InvoiceResponse?> GetByBillIdAsync(int billId, CancellationToken cancellationToken = default)
    {
        var bundle = await _reader.ReadOutwardByBillIdAsync(billId, cancellationToken);
        if (bundle is null) return null;

        var account = await _carol.Accounts.FirstOrDefaultAsync(a => a.AccountId == bundle.Header.AccountId, cancellationToken);
        var (docToCompany, names) = await FetchCompanyMapsAsync(cancellationToken);
        var docToPrefix = await _carol.DocIdToPrefixMapAsync(cancellationToken);
        var roundOffs = await _reader.ReadRoundOffAsync(new[] { bundle.Header.BillId }, cancellationToken);
        var response = MapToInvoiceResponse(bundle.Header, account, bundle.Lines, new HashSet<string>(), docToCompany, names, docToPrefix,
            roundOffs.TryGetValue(bundle.Header.BillId, out var ro) ? ro : null);
        await ApplyEInvoiceStatusAsync(new[] { response }, cancellationToken);
        return response;
    }

    private async Task<Dictionary<short, CarolAccount>> FetchAccountsAsync(IReadOnlyList<CarolDocBundle> bundles, CancellationToken ct)
    {
        var accountIds = bundles.Select(b => b.Header.AccountId).Distinct().ToList();
        if (accountIds.Count == 0) return new Dictionary<short, CarolAccount>();
        return await _carol.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.AccountId))
            .ToDictionaryAsync(a => a.AccountId, ct);
    }

    private static CarolAccount? Lookup(Dictionary<short, CarolAccount> accounts, short id)
        => accounts.TryGetValue(id, out var a) ? a : null;

    private async Task ApplyEInvoiceStatusAsync(IReadOnlyList<InvoiceResponse> invoices, CancellationToken cancellationToken)
    {
        if (invoices.Count == 0) return;
        var billIds = invoices.Select(i => i.BillId).ToList();
        var irnRows = await _db.IRNRecords.AsNoTracking()
            .Where(r => r.BillId != null && billIds.Contains(r.BillId!.Value) && r.Status == IRNStatus.Generated)
            .Select(r => new { BillId = r.BillId!.Value, r.IRNNumber })
            .ToListAsync(cancellationToken);
        // One IRN per bill (latest wins if duplicates exist).
        var irnByBill = irnRows
            .GroupBy(r => r.BillId)
            .ToDictionary(g => g.Key, g => g.Last().IRNNumber);
        foreach (var inv in invoices)
        {
            var hasIrn = irnByBill.TryGetValue(inv.BillId, out var irn);
            inv.Irn = hasIrn ? irn ?? string.Empty : string.Empty;
            inv.EInvoiceStatus = hasIrn
                ? "Done"
                : inv.TotalAmount > EInvoiceThreshold ? "Required" : "NA";
        }
    }

    // header.TotalAmt is the pre-tax (often foreign-currency) amount, so the
    // invoice total must be taxable + tax. Fall back to header*rate only when
    // there are no line rows to tax.
    // roundOff is the signed invoice-level adjustment (round-off / misc charges)
    // from Bill_Tax. It applies only to the line-derived total; in the no-lines
    // fallback the header TotalAmt is already the ERP's final (rounded) amount.
    private static (decimal Taxable, decimal Igst, decimal Cgst, decimal Sgst, decimal Total) ComputeFigures(
        CarolDocHeader header, List<CarolSalesLine> lines, decimal roundOff = 0m)
    {
        var rate = header.ExchRate == 0 ? 1m : header.ExchRate;
        var lineTaxable = decimal.Round(lines.Sum(l => l.TaxableInr), 2);
        var igst = decimal.Round(lines.Sum(l => l.IgstAmount), 2);
        var cgst = decimal.Round(lines.Sum(l => l.CgstAmount), 2);
        var sgst = decimal.Round(lines.Sum(l => l.SgstAmount), 2);
        var headerInr = decimal.Round(header.TotalAmt * rate, 2);
        var taxable = lineTaxable == 0m ? headerInr : lineTaxable;
        var total = lineTaxable == 0m ? headerInr : decimal.Round(taxable + igst + cgst + sgst + roundOff, 2);
        return (taxable, igst, cgst, sgst, total);
    }

    private static InvoiceResponse MapToInvoiceResponse(
        CarolDocHeader header,
        CarolAccount? account,
        List<CarolSalesLine> lines,
        HashSet<string> duplicateNumbers,
        IReadOnlyDictionary<short, byte> docToCompany,
        IReadOnlyDictionary<byte, string> companyNames,
        IReadOnlyDictionary<short, string> docToPrefix,
        CarolDocumentReader.RoundOffInfo? roundOff)
    {
        var f = ComputeFigures(header, lines, roundOff?.Amount ?? 0m);
        // Discount = gross line Amount - taxable, summed over lines. Uses the
        // stored gross Amount (consistent column name) rather than Rate x Qty,
        // which is unreliable when Rate is a list/MRP price (e.g. intercompany
        // transfers, where Rate x Qty wildly overstates the discount). Still
        // avoids the per-install discount column (DiscAmt/DiscAmount). Clamp <0.
        var grossLines = decimal.Round(lines.Sum(l => l.GrossInr), 2);
        var discount = grossLines > f.Taxable ? decimal.Round(grossLines - f.Taxable, 2) : 0m;
        byte? coId = header.DocId is short d && docToCompany.TryGetValue(d, out var c) ? c : null;
        var coName = coId.HasValue && companyNames.TryGetValue(coId.Value, out var n) ? n : null;
        return new InvoiceResponse
        {
            Id = DeterministicGuid(header.BillId),
            BillId = header.BillId,
            InvoiceNumber = BuildInvoiceNumber(header, duplicateNumbers, docToPrefix),
            InvoiceDate = header.BillDate,
            PartyName = ResolvePartyName(header, account),
            PartyGSTIN = PartyGstinLabel(header, account),
            PlaceOfSupply = header.SupplyType ?? string.Empty,
            PosStateCode = GstStateCode(account?.StateId),
            Section = ClassifySection(header, account, f.Igst, f.Total),
            GstCategory = header.GstCategory,
            TaxableValue = f.Taxable,
            Discount = discount,
            CGST = f.Cgst,
            SGST = f.Sgst,
            IGST = f.Igst,
            RoundOff = roundOff?.Amount ?? 0m,
            RoundOffLabel = roundOff?.Label ?? string.Empty,
            TotalAmount = f.Total,
            CompanyId = coId,
            CompanyName = coName,
            Lines = lines.Select(l => new InvoiceLineResponse
            {
                Id = DeterministicGuid(l.LineSl),
                Description = l.Description,
                HSNCode = l.Hsn,
                Quantity = l.Quantity,
                Rate = l.Rate,
                TaxableValue = l.TaxableInr,
                GstRate = l.IgstRate,
                CGST = l.CgstAmount,
                SGST = l.SgstAmount,
                IGST = l.IgstAmount,
                Total = decimal.Round(l.TaxableInr + l.IgstAmount + l.CgstAmount + l.SgstAmount, 2),
            }).ToList(),
        };
    }

    private static string CoreInvoiceNumber(CarolDocHeader header)
    {
        if (!string.IsNullOrWhiteSpace(header.InvNo)) return header.InvNo!;
        if (header.BillNumber.HasValue)
            return header.BillNumber.Value.ToString() + (header.Suffix ?? string.Empty);
        return string.Empty;
    }

    // Printed invoice number: document-series Prefix + "/" + the core number
    // (e.g. "CC" + "236" => "CC/236"). A "/" separator is inserted only when the
    // prefix doesn't already end in one, so slash-terminated series like
    // "CC/SB/" yield "CC/SB/237" (no double slash). Falls back to the bare core
    // number when no prefix is configured for the doctype. The internal BillId is
    // appended ONLY when the full prefixed number genuinely collides with another
    // bill (rare) — never just because the bare number repeats across series.
    private static string BuildInvoiceNumber(CarolDocHeader header, HashSet<string> duplicateNumbers, IReadOnlyDictionary<short, string> docToPrefix)
    {
        var core = CoreInvoiceNumber(header);
        if (string.IsNullOrWhiteSpace(core)) return $"BILL-{header.BillId}";
        var full = ApplyPrefix(core, header, docToPrefix);
        if (string.IsNullOrWhiteSpace(header.InvNo) && duplicateNumbers.Contains(full))
            return $"{full}/{header.BillId}";
        return full;
    }

    // The full prefixed number without any dedup suffix — the duplicate-detection
    // key. CC/17 and CCR/17 produce different keys, so distinct series aren't
    // mistaken for duplicates.
    private static string FullNumberKey(CarolDocHeader header, IReadOnlyDictionary<short, string> docToPrefix)
    {
        var core = CoreInvoiceNumber(header);
        return string.IsNullOrWhiteSpace(core) ? string.Empty : ApplyPrefix(core, header, docToPrefix);
    }

    private static string ApplyPrefix(string core, CarolDocHeader header, IReadOnlyDictionary<short, string> docToPrefix)
    {
        var prefix = header.DocId is short d && docToPrefix.TryGetValue(d, out var p) ? p?.Trim() : null;
        if (string.IsNullOrWhiteSpace(prefix)) return core;
        var sep = prefix!.EndsWith("/") ? string.Empty : "/";
        return $"{prefix}{sep}{core}";
    }

    // The buyer name shown on the invoice. For cash / walk-in sales the Account
    // ledger is the generic "Cash" account, so the real customer name lives on
    // the bill header (OtherRef/Title/TosName, captured as Header.CustomerRef) —
    // prefer it over the bland "Cash" ledger name.
    private static string ResolvePartyName(CarolDocHeader header, CarolAccount? account)
    {
        if (LooksLikeCashLedger(account) && !string.IsNullOrWhiteSpace(header.CustomerRef))
            return header.CustomerRef!.Trim();
        return account?.AccountName ?? string.Empty;
    }

    private static bool LooksLikeCashLedger(CarolAccount? account)
    {
        var name = account?.AccountName?.Trim();
        return string.IsNullOrEmpty(name) || name.Equals("Cash", StringComparison.OrdinalIgnoreCase);
    }

    // Foreign / export customers genuinely have no GSTIN. We label them
    // explicitly so the user can tell "no data" apart from "by design". The
    // GSTIN may sit on the Account or, for cash/walk-in bills, on the header.
    private static string PartyGstinLabel(CarolDocHeader header, CarolAccount? account)
    {
        if (IsValidGstin(account?.GstNo)) return NormalizeGstin(account?.GstNo);
        if (IsValidGstin(header.GstNo)) return NormalizeGstin(header.GstNo);
        if (LooksLikeExport(header, account)) return "Export";
        return "Unregistered";
    }

    // Threshold (Rs.) above which an inter-state B2C invoice is reported B2CL
    // (invoice-wise) rather than B2CS (rate-wise). Notification 12/2024-CT
    // (10 Jul 2024) cut this from Rs 2,50,000 to Rs 1,00,000 for supplies made
    // on or after 1 Aug 2024. Kept period-aware so historical periods and
    // amendments still classify against the threshold in force at the time.
    // Values come from GstRules config (ApplyGstRules), defaulting to the
    // statutory figures so a missing config is still correct.
    private static readonly DateTime B2clThresholdCutover = new(2024, 8, 1);
    private static decimal _b2clThresholdPreAug2024 = 250_000m;
    private static decimal _b2clThresholdPostAug2024 = 100_000m;

    // Applied once at startup from configuration (see Program.cs).
    public static void ApplyGstRules(GstRulesOptions options)
    {
        _b2clThresholdPreAug2024 = options.B2CLThreshold.PreAug2024;
        _b2clThresholdPostAug2024 = options.B2CLThreshold.PostAug2024;
    }

    private static decimal B2clThresholdFor(DateTime invoiceDate)
        => invoiceDate.Date >= B2clThresholdCutover ? _b2clThresholdPostAug2024 : _b2clThresholdPreAug2024;

    // GSTR-1 section for one document: credit/debit notes first (a document-type
    // thing), then export (export mapping or a no-GSTIN foreign party), then B2B
    // when the buyer carries a real GSTIN. Unregistered (B2C) splits into B2CL
    // (inter-state — IGST present — and invoice value over the threshold) vs B2CS.
    private static string ClassifySection(CarolDocHeader header, CarolAccount? account, decimal igst, decimal total)
    {
        if (IsCreditDebitNote(header.GstCategory)) return "CDN";
        if (header.GstCategory == GstDocumentCatalog.ExportSales
            || PartyGstinLabel(header, account) == "Export") return "Export";
        if (HasRealGstin(header, account)) return "B2B";
        return igst > 0m && total > B2clThresholdFor(header.BillDate) ? "B2CL" : "B2CS";
    }

    // GSTR-1 Table 12 tab for a supply: B2B (registered recipient, export, SEZ)
    // vs B2C (unregistered — B2CL/B2CS). Credit/debit notes follow their
    // counterparty's registration. Portal cross-validates Table 12 B2B/B2C
    // against the matching invoice tables, so this must mirror ClassifySection.
    private static string HsnSupplyType(string? section, string? partyGstin)
        => section switch
        {
            "Export" => "B2B",
            // A "B2B" row with no real GSTIN isn't B2B (see
            // NormalizeUnregisteredSections). Table 12 has to split it the same
            // way the invoice tables do, or the HSN summary won't tie back.
            "B2B" => IsValidGstin(partyGstin) ? "B2B" : "B2C",
            "B2CL" or "B2CS" => "B2C",
            _ => IsValidGstin(partyGstin) ? "B2B" : "B2C", // CDN and anything else
        };

    // Canonicalize an HSN/SAC code for GSTR-1 Table 12: digits only, reported at
    // a valid GSTN level (8, else 6, else 4). Never emits placeholders like "NA"
    // or "Not Defined" — GSTN rejects those and the mandatory Table-12 HSN is a
    // dropdown, not free text. Returns "" when the source has no usable code, so
    // the line surfaces as a missing-HSN data error rather than a bogus value.
    // KSCC (AATO > 5cr) needs a 6-digit minimum; 8-digit source codes already
    // satisfy it and are kept as-is.
    private static string NormalizeHsn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length >= 8) return digits[..8];
        if (digits.Length >= 6) return digits[..6];
        if (digits.Length >= 4) return digits[..4];
        return string.Empty;
    }

    private static bool IsCreditDebitNote(string? category)
        => category == GstDocumentCatalog.CreditNote
        || category == GstDocumentCatalog.DebitNote
        || category == GstDocumentCatalog.SalesDebitNote;

    // B2B only when a *valid* GSTIN is present on the buyer — on the Account
    // ledger OR on the bill header (cash/walk-in bills often carry the buyer
    // GSTIN on the header, not the generic Cash account). Blank, "NIL"/"NA"
    // placeholders and malformed values count as no GSTIN ⇒ B2C.
    private static bool HasRealGstin(CarolDocHeader header, CarolAccount? account)
        => IsValidGstin(account?.GstNo) || IsValidGstin(header.GstNo);

    // A real GSTIN is exactly 15 alphanumeric characters. This rejects blanks
    // and ERP placeholders like "NIL"/"NA" that would otherwise be mistaken for
    // a registered buyer and wrongly classified as B2B.
    private static bool IsValidGstin(string? raw)
    {
        var g = NormalizeGstin(raw);
        return g.Length == 15 && g.All(char.IsLetterOrDigit);
    }

    // CarolERP Account.StateId follows the GST/SQL state-code convention (01-37,
    // plus 38 Ladakh and 97 Other Territory); null for foreign parties. Format
    // it as the 2-digit place-of-supply code, or "" when it isn't a valid
    // domestic GST state code (so POS safely falls back to the seller's state
    // downstream — no regression).
    private static string GstStateCode(byte? stateId)
        => stateId is byte s && ((s is >= 1 and <= 38) || s == 97)
            ? s.ToString("D2")
            : string.Empty;

    private static bool LooksLikeExport(CarolDocHeader header, CarolAccount? account)
    {
        // Foreign customer = a country other than India (CountryId 1).
        // We deliberately do NOT infer export from a missing StateId: KSCC
        // ignores Account.StateId (it's always null), which previously flagged
        // every domestic cash sale as "Export". Genuine exports are still
        // classified via header.GstCategory == ExportSales (the export mapping).
        if (account?.CountryId is byte country && country != 1) return true;
        var s = header.SupplyType ?? string.Empty;
        if (s.Contains("EXP", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Contains("OVERSEAS", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Guid DeterministicGuid(int id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[12..], id);
        return new Guid(bytes);
    }

    private static string NormalizeGstin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return cleaned.Length <= 15 ? cleaned : cleaned[..15];
    }
}
