using GSTAutoPilot.Application.DTOs;
using GSTAutoPilot.Application.Services;
using GSTAutoPilot.Infrastructure.CarolERP;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Services;

public class PurchaseInvoiceService : IPurchaseInvoiceService
{
    private readonly CarolERPDbContext _carol;
    private readonly SpInwardService _spInward;

    public PurchaseInvoiceService(CarolERPDbContext carol, SpInwardService spInward)
    {
        _carol = carol;
        _spInward = spInward;
    }

    public async Task<IReadOnlyList<PurchaseInvoiceResponse>> ListAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        // Prefer the inward stored procedure when configured — it's the source of
        // truth for purchases, owning all GST logic. Falls through to the table
        // path only when no inward SP is set.
        if (_spInward.IsConfigured)
            return await _spInward.ListAsync(year, month, cancellationToken);

        // Scope to the requested month FIRST — reading every purchase ever is
        // both wrong for a period view and far too slow on large installs (KSCC),
        // which previously timed out.
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        // Apply the sanction filter so unapproved purchase bills don't appear
        // in the purchase invoice list (same rule as the unified outward /
        // inward reads — see CarolDocumentReader.ApplySanctionFilter).
        var sanctionIds = await _carol.SanctionRequiredDocIdsAsync(cancellationToken);
        var q = _carol.PurchaseHeaders.AsNoTracking()
            .Where(h => h.BillDate >= start && h.BillDate < end);

        // Restrict to the tenant's INWARD (purchase) document mappings so the
        // list shows only real purchases — NOT every Bill_Mas row. Without this,
        // sales bills and expense doctypes (e.g. the 540 utility/electricity
        // bills, which are not mapped as purchases) leaked into the list.
        var inwardMappings = _carol.ActiveInwardMappings;
        // Configured inward mappings but all disabled => the tenant means "no
        // purchases here, use the inward SP" — return nothing rather than the
        // whole Bill_Mas table. Only a genuinely unseeded tenant (no inward rows
        // at all) falls through to the legacy unfiltered read.
        if (inwardMappings.Count == 0 && _carol.HasAnyInwardMappings)
        {
            return new List<PurchaseInvoiceResponse>();
        }
        if (inwardMappings.Count > 0)
        {
            var inwardDocIds = new HashSet<short>();
            var anyUnfiltered = false;
            foreach (var m in inwardMappings)
            {
                var ids = await _carol.ResolveDocIdsAsync(m.DocTypes, m.SubTypes, cancellationToken);
                if (ids is null) { anyUnfiltered = true; break; } // no doctype filter ⇒ all
                foreach (var id in ids) inwardDocIds.Add(id);
            }
            if (!anyUnfiltered)
            {
                var arr = inwardDocIds.ToArray();
                q = arr.Length == 0
                    ? q.Where(_ => false)
                    : q.Where(h => h.DocId != null && arr.Contains(h.DocId.Value));
            }
        }
        if (sanctionIds.Count > 0)
        {
            var ids = sanctionIds.ToArray();
            q = q.Where(h => h.DocId == null || !ids.Contains(h.DocId.Value) || h.Sanctioned == 1);
        }
        // Same X-Company-Id gate the unified reader applies, with the same
        // GST-group expansion (see CarolDocumentReader.ResolveCompanyDocIds
        // for the rationale).
        if (_carol.ActiveCompanyId is byte coId)
        {
            var groups = await _carol.CompanyGroupsAsync(cancellationToken);
            var group = groups.FirstOrDefault(g => g.MemberCoIds.Contains(coId));
            if (group is null)
            {
                q = q.Where(_ => false);
            }
            else
            {
                var union = new HashSet<short>();
                foreach (var member in group.MemberCoIds)
                {
                    foreach (var d in await _carol.DocIdsForCompanyAsync(member, cancellationToken))
                        union.Add(d);
                }
                var arr = union.ToArray();
                q = arr.Length == 0
                    ? q.Where(_ => false)
                    : q.Where(h => h.DocId != null && arr.Contains(h.DocId.Value));
            }
        }
        var headers = await q
            .OrderByDescending(h => h.BillDate)
            .ToListAsync(cancellationToken);

        var billIds = headers.Select(h => h.BillId).ToList();
        var lines = await _carol.PurchaseLines
            .AsNoTracking()
            .Where(l => billIds.Contains(l.BillId))
            .ToListAsync(cancellationToken);
        var linesByBill = lines.GroupBy(l => l.BillId).ToDictionary(g => g.Key, g => g.ToList());

        var accountIds = headers.Select(h => h.AccountId).Distinct().ToList();
        var accounts = await _carol.Accounts
            .AsNoTracking()
            .Where(a => accountIds.Contains(a.AccountId))
            .ToDictionaryAsync(a => a.AccountId, cancellationToken);

        return headers.Select(h =>
        {
            var ls = linesByBill.TryGetValue(h.BillId, out var lst) ? lst : new();
            accounts.TryGetValue(h.AccountId, out var acc);

            var taxable = decimal.Round(ls.Sum(l => l.Amount), 2);
            var cgst = decimal.Round(ls.Sum(l => l.CGSTAmt ?? 0m), 2);
            var sgst = decimal.Round(ls.Sum(l => l.SGSTAmt ?? 0m), 2);
            var igst = decimal.Round(ls.Sum(l => l.IGSTAmt ?? 0m), 2);

            decimal gstRate;
            if (igst > 0m)
            {
                gstRate = ls.Where(l => l.IgstRate > 0m).Select(l => l.IgstRate).DefaultIfEmpty(0m).Max();
            }
            else
            {
                var c = ls.Where(l => l.CgstRate > 0m).Select(l => l.CgstRate).DefaultIfEmpty(0m).Max();
                var s = ls.Where(l => l.SgstRate > 0m).Select(l => l.SgstRate).DefaultIfEmpty(0m).Max();
                gstRate = c + s;
            }

            return new PurchaseInvoiceResponse
            {
                PurchaseInvoiceId = DeterministicGuid(h.BillId),
                SupplierName = acc?.AccountName ?? h.AcName ?? string.Empty,
                SupplierGSTIN = NormalizeGstin(h.GstNo ?? acc?.GstNo),
                InvoiceNo = h.InvNo ?? $"CAROL-{h.BillId}",
                InvoiceDate = h.BillDate,
                TaxableAmount = taxable,
                CGSTAmount = cgst,
                SGSTAmount = sgst,
                IGSTAmount = igst,
                TotalAmount = decimal.Round(h.TotalAmt, 2),
                GSTRate = gstRate,
                IsITCEligible = h.GstReverse == 0,
                CreatedOn = h.BillDate,
            };
        }).ToList();
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
