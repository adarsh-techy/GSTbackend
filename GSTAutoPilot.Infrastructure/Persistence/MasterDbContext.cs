using GSTAutoPilot.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GSTAutoPilot.Infrastructure.Persistence;

public class MasterDbContext : DbContext
{
    public MasterDbContext(DbContextOptions<MasterDbContext> options) : base(options)
    {
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();
    public DbSet<DocumentMapping> DocumentMappings => Set<DocumentMapping>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(entity =>
        {
            // The Tenants table carries an enabled audit trigger
            // (trg_Tenants_Audit) in the master DB. SQL Server rejects the
            // implicit OUTPUT clause EF Core emits for UPDATE/INSERT on a
            // triggered table, so any save (e.g. Settings → SP Profile writing
            // OutwardSP/InwardSP) threw DbUpdateException. Declaring the trigger
            // makes EF switch to the trigger-compatible SQL that omits OUTPUT.
            entity.ToTable("Tenants", tb => tb.HasTrigger("trg_Tenants_Audit"));
            entity.HasKey(t => t.TenantId);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(200);
            entity.Property(t => t.GSTIN).IsRequired().HasMaxLength(15);
            entity.Property(t => t.ConnectionString).IsRequired().HasMaxLength(500);
            entity.Property(t => t.CarolERPConnection).HasMaxLength(500);
            entity.Property(t => t.SalesHeaderTable).HasMaxLength(128);
            entity.Property(t => t.SalesLineTable).HasMaxLength(128);
            entity.Property(t => t.CarolErpFlavor).HasMaxLength(32).HasDefaultValue("Default");
            entity.HasIndex(t => t.GSTIN).IsUnique();
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.HasKey(u => u.UserRoleId);
            entity.Property(u => u.EmplCode).IsRequired().HasMaxLength(50);
            entity.Property(u => u.DisplayName).HasMaxLength(100);
            entity.Property(u => u.Role).IsRequired().HasMaxLength(50);
            entity.HasIndex(u => new { u.TenantId, u.EmplCode }).IsUnique();
        });

        modelBuilder.Entity<TenantSettings>(entity =>
        {
            entity.HasKey(t => t.SettingId);
            entity.Property(t => t.LogoPath).HasMaxLength(500);
            entity.Property(t => t.InvoiceFooterText).HasMaxLength(500);
            // Filtered unique on (TenantId) WHERE CompanyId IS NULL — exactly
            // one tenant-default row per tenant. Plus filtered unique on
            // (TenantId, CompanyId) WHERE CompanyId IS NOT NULL — at most one
            // override per (tenant, GST group). Together these let the table
            // hold the existing tenant-default AND zero-or-more per-company
            // override rows without violating uniqueness.
            entity.HasIndex(t => t.TenantId)
                .IsUnique()
                .HasFilter("[CompanyId] IS NULL")
                .HasDatabaseName("UX_TenantSettings_Tenant_Default");
            entity.HasIndex(t => new { t.TenantId, t.CompanyId })
                .IsUnique()
                .HasFilter("[CompanyId] IS NOT NULL")
                .HasDatabaseName("UX_TenantSettings_Tenant_Company");
        });

        modelBuilder.Entity<DocumentMapping>(entity =>
        {
            entity.HasKey(d => d.MappingId);
            entity.Property(d => d.GstCategory).IsRequired().HasMaxLength(50);
            entity.Property(d => d.DisplayName).IsRequired().HasMaxLength(100);
            entity.Property(d => d.HeaderTable).IsRequired().HasMaxLength(50);
            entity.Property(d => d.LineTable).IsRequired().HasMaxLength(50);
            entity.Property(d => d.DocTypes).HasMaxLength(100);
            entity.Property(d => d.SubTypes).HasMaxLength(100);
            entity.Property(d => d.TaxMode).IsRequired().HasMaxLength(10);
            entity.HasIndex(d => new { d.TenantId, d.GstCategory }).IsUnique();
        });
    }
}
