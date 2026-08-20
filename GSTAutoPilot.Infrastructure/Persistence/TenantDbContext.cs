using GSTAutoPilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Persistence;

public class TenantDbContext : DbContext
{
    public TenantDbContext(DbContextOptions<TenantDbContext> options) : base(options)
    {
    }

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<GSTR2B> GSTR2BRecords => Set<GSTR2B>();
    public DbSet<ReconResult> ReconResults => Set<ReconResult>();
    public DbSet<IRNRecord> IRNRecords => Set<IRNRecord>();
    public DbSet<EWayBill> EWayBills => Set<EWayBill>();
    public DbSet<GSTINValidation> GSTINValidations => Set<GSTINValidation>();
    public DbSet<Gstr1Filing> Gstr1Filings => Set<Gstr1Filing>();
    public DbSet<Gstr3bFiling> Gstr3bFilings => Set<Gstr3bFiling>();
    public DbSet<BillOfEntry> BillsOfEntry => Set<BillOfEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.InvoiceNumber).IsRequired().HasMaxLength(50);
            entity.Property(i => i.PartyName).IsRequired().HasMaxLength(200);
            entity.Property(i => i.PartyGSTIN).HasMaxLength(15);
            entity.Property(i => i.PlaceOfSupply).HasMaxLength(50);
            entity.Property(i => i.TaxableValue).HasPrecision(18, 2);
            entity.Property(i => i.CGST).HasPrecision(18, 2);
            entity.Property(i => i.SGST).HasPrecision(18, 2);
            entity.Property(i => i.IGST).HasPrecision(18, 2);
            entity.Property(i => i.TotalAmount).HasPrecision(18, 2);
            entity.HasIndex(i => i.InvoiceNumber).IsUnique();

            entity.HasMany(i => i.Lines)
                  .WithOne(l => l.Invoice)
                  .HasForeignKey(l => l.InvoiceId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InvoiceLine>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Description).IsRequired().HasMaxLength(500);
            entity.Property(l => l.HSNCode).HasMaxLength(20);
            entity.Property(l => l.Quantity).HasPrecision(18, 3);
            entity.Property(l => l.Rate).HasPrecision(18, 4);
            entity.Property(l => l.TaxableValue).HasPrecision(18, 2);
            entity.Property(l => l.GstRate).HasPrecision(5, 2);
            entity.Property(l => l.CGST).HasPrecision(18, 2);
            entity.Property(l => l.SGST).HasPrecision(18, 2);
            entity.Property(l => l.IGST).HasPrecision(18, 2);
            entity.Property(l => l.Total).HasPrecision(18, 2);
        });

        modelBuilder.Entity<PurchaseInvoice>(entity =>
        {
            entity.HasKey(p => p.PurchaseInvoiceId);
            entity.Property(p => p.SupplierName).IsRequired().HasMaxLength(200);
            entity.Property(p => p.SupplierGSTIN).HasMaxLength(15);
            entity.Property(p => p.InvoiceNo).IsRequired().HasMaxLength(50);
            entity.Property(p => p.TaxableAmount).HasPrecision(18, 2);
            entity.Property(p => p.IGSTAmount).HasPrecision(18, 2);
            entity.Property(p => p.CGSTAmount).HasPrecision(18, 2);
            entity.Property(p => p.SGSTAmount).HasPrecision(18, 2);
            entity.Property(p => p.TotalAmount).HasPrecision(18, 2);
            entity.Property(p => p.GSTRate).HasPrecision(5, 2);
            entity.HasIndex(p => new { p.SupplierGSTIN, p.InvoiceNo });
        });

        modelBuilder.Entity<GSTR2B>(entity =>
        {
            entity.HasKey(g => g.GSTR2BId);
            entity.Property(g => g.SupplierGSTIN).IsRequired().HasMaxLength(15);
            entity.Property(g => g.SupplierName).HasMaxLength(200);
            entity.Property(g => g.InvoiceNo).IsRequired().HasMaxLength(50);
            entity.Property(g => g.TaxableAmount).HasPrecision(18, 2);
            entity.Property(g => g.IGSTAmount).HasPrecision(18, 2);
            entity.Property(g => g.CGSTAmount).HasPrecision(18, 2);
            entity.Property(g => g.SGSTAmount).HasPrecision(18, 2);
            entity.Property(g => g.FilingPeriod).IsRequired().HasMaxLength(6);
            entity.Property(g => g.RecordType).IsRequired().HasMaxLength(10);
            entity.Property(g => g.Source).HasMaxLength(40);
            entity.Property(g => g.ItcIneligibleReason).HasMaxLength(200);
            entity.HasIndex(g => new { g.FilingPeriod, g.SupplierGSTIN, g.InvoiceNo });
        });

        modelBuilder.Entity<ReconResult>(entity =>
        {
            entity.HasKey(r => r.ReconId);
            entity.Property(r => r.SupplierGSTIN).IsRequired().HasMaxLength(15);
            entity.Property(r => r.InvoiceNo).IsRequired().HasMaxLength(50);
            entity.Property(r => r.GSTR2BAmount).HasPrecision(18, 2);
            entity.Property(r => r.BooksAmount).HasPrecision(18, 2);
            entity.Property(r => r.Difference).HasPrecision(18, 2);
            entity.Property(r => r.Status).IsRequired().HasMaxLength(20);
            entity.Property(r => r.AIRemarks).HasMaxLength(1000);
            entity.Property(r => r.FilingPeriod).IsRequired().HasMaxLength(6);
            entity.Property(r => r.Section).IsRequired().HasMaxLength(10);
            entity.HasIndex(r => new { r.FilingPeriod, r.Status });
        });

        modelBuilder.Entity<IRNRecord>(entity =>
        {
            entity.HasKey(r => r.IRNId);
            entity.Property(r => r.InvoiceNo).IsRequired().HasMaxLength(50);
            entity.Property(r => r.IRNNumber).IsRequired().HasMaxLength(64);
            entity.Property(r => r.AcknowledgementNo).IsRequired().HasMaxLength(20);
            entity.Property(r => r.QRCode).IsRequired();
            entity.Property(r => r.SignedInvoice).IsRequired();
            entity.Property(r => r.Status).IsRequired().HasMaxLength(20);
            entity.Property(r => r.CancelReason).HasMaxLength(500);
            entity.Property(r => r.CancelRemarks).HasMaxLength(500);
            entity.Property(r => r.EmailSentTo).HasMaxLength(200);
            entity.HasIndex(r => r.InvoiceId);
            entity.HasIndex(r => r.IRNNumber).IsUnique();
        });

        modelBuilder.Entity<GSTINValidation>(entity =>
        {
            entity.HasKey(v => v.ValidationId);
            entity.Property(v => v.GSTIN).IsRequired().HasMaxLength(15);
            entity.Property(v => v.TradeName).HasMaxLength(200);
            entity.Property(v => v.LegalName).HasMaxLength(200);
            entity.Property(v => v.State).HasMaxLength(100);
            entity.Property(v => v.StateCode).HasMaxLength(2);
            entity.Property(v => v.TaxpayerType).HasMaxLength(50);
            entity.Property(v => v.Status).IsRequired().HasMaxLength(20);
            entity.Property(v => v.FilingFrequency).HasMaxLength(20);
            entity.Property(v => v.LastFiledReturn).HasMaxLength(50);
            entity.Property(v => v.Source).IsRequired().HasMaxLength(20);
            entity.HasIndex(v => new { v.GSTIN, v.ValidatedOn });
        });

        modelBuilder.Entity<Gstr1Filing>(entity =>
        {
            entity.HasKey(f => f.FilingId);
            entity.Property(f => f.Period).IsRequired().HasMaxLength(6);
            entity.Property(f => f.Status).IsRequired().HasMaxLength(20);
            entity.Property(f => f.AckNo).HasMaxLength(50);
            entity.Property(f => f.ReferenceId).HasMaxLength(100);
            entity.Property(f => f.FiledBy).HasMaxLength(200);
            entity.HasIndex(f => new { f.Period, f.Status });
        });

        modelBuilder.Entity<Gstr3bFiling>(entity =>
        {
            entity.HasKey(f => f.FilingId);
            entity.Property(f => f.Period).IsRequired().HasMaxLength(6);
            entity.Property(f => f.Status).IsRequired().HasMaxLength(20);
            entity.Property(f => f.AckNo).HasMaxLength(50);
            entity.Property(f => f.ReferenceId).HasMaxLength(100);
            entity.Property(f => f.FiledBy).HasMaxLength(200);
            entity.Property(f => f.Cin).HasMaxLength(50);
            entity.HasIndex(f => new { f.Period, f.Status });
        });

        modelBuilder.Entity<BillOfEntry>(entity =>
        {
            entity.HasKey(b => b.BoEId);
            entity.Property(b => b.Period).IsRequired().HasMaxLength(6);
            entity.Property(b => b.BoENumber).IsRequired().HasMaxLength(50);
            entity.Property(b => b.PortCode).HasMaxLength(20);
            entity.Property(b => b.SupplierName).HasMaxLength(200);
            entity.Property(b => b.SupplierGSTIN).HasMaxLength(15);
            entity.Property(b => b.AssessableValue).HasPrecision(18, 2);
            entity.Property(b => b.IGSTAmount).HasPrecision(18, 2);
            entity.Property(b => b.CessAmount).HasPrecision(18, 2);
            entity.Property(b => b.Remarks).HasMaxLength(500);
            entity.HasIndex(b => b.Period);
        });

        modelBuilder.Entity<EWayBill>(entity =>
        {
            entity.HasKey(e => e.EWBId);
            entity.Property(e => e.EWBNumber).IsRequired().HasMaxLength(12);
            entity.Property(e => e.FromGSTIN).IsRequired().HasMaxLength(15);
            entity.Property(e => e.FromAddress).HasMaxLength(500);
            entity.Property(e => e.ToGSTIN).HasMaxLength(15);
            entity.Property(e => e.ToAddress).HasMaxLength(500);
            entity.Property(e => e.TransporterGSTIN).HasMaxLength(15);
            entity.Property(e => e.TransporterName).HasMaxLength(200);
            entity.Property(e => e.VehicleNumber).HasMaxLength(20);
            entity.Property(e => e.Distance).HasPrecision(10, 2);
            entity.Property(e => e.Mode).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CancelReason).HasMaxLength(500);
            entity.HasIndex(e => e.InvoiceId);
            entity.HasIndex(e => e.EWBNumber).IsUnique();
        });
    }
}
