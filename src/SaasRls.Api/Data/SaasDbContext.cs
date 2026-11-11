using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SaasRls.Api.Data;

public sealed class Customer
{
    public Guid Id { get; set; }

    /// <summary>Set by the database from the transaction's tenant (column default); the application never writes it.</summary>
    public Guid TenantId { get; private set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    public DateTime CreatedAt { get; private set; }
}

public sealed class Order
{
    public Guid Id { get; set; }

    public Guid TenantId { get; private set; }

    public Guid CustomerId { get; set; }

    public long Number { get; set; }

    public required string Status { get; set; }

    public long TotalCents { get; set; }

    public DateTime CreatedAt { get; private set; }
}

/// <summary>
/// EF Core over the tenant tables. There is deliberately no global query filter on TenantId: the database enforces
/// isolation, and a filter in the application would only hide a broken policy from the tests that should catch it.
/// </summary>
public sealed class SaasDbContext(DbContextOptions<SaasDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("app");

        modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("customers");
            customer.HasKey(c => c.Id);
            customer.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            TenantColumn(customer.Property(c => c.TenantId));
            customer.Property(c => c.Name).HasColumnName("name");
            customer.Property(c => c.Email).HasColumnName("email");
            customer.Property(c => c.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders");
            order.HasKey(o => o.Id);
            order.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
            TenantColumn(order.Property(o => o.TenantId));
            order.Property(o => o.CustomerId).HasColumnName("customer_id");
            order.Property(o => o.Number).HasColumnName("number");
            order.Property(o => o.Status).HasColumnName("status");
            order.Property(o => o.TotalCents).HasColumnName("total_cents");
            order.Property(o => o.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        });
    }

    private static void TenantColumn(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<Guid> property)
    {
        // Omitted from INSERT (the column default reads the session variable) and never part of an UPDATE.
        property.HasColumnName("tenant_id").HasDefaultValueSql("app.current_tenant_id()").ValueGeneratedOnAdd();
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
