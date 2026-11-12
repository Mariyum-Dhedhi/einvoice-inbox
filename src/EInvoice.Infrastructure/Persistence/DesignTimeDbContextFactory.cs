using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EInvoice.Infrastructure.Persistence;

/// <summary>
/// Used only by the dotnet-ef tool. Creating migrations and SQL scripts does not connect to a database,
/// so no real connection string or secret is needed here.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<EInvoiceDbContext>
{
    public EInvoiceDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<EInvoiceDbContext>()
            .UseSqlServer("Server=design-time-only;Database=EInvoice")
            .Options);
}
