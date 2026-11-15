using EInvoice.Application.Security;
using EInvoice.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EInvoice.Infrastructure.Persistence.Configurations;

/// <summary>
/// Identity's tables under our rules: own names, a Guid key that is nonclustered, a clustered RowId, and an explicit
/// length for every string (Identity leaves several as nvarchar(max)). Runs after Identity's own model setup.
/// </summary>
internal static class IdentityLengths
{
    public const int Name = 256;      // user names, e-mail addresses, role names (Identity's own limit)
    public const int Stamp = 64;      // security and concurrency stamps (a Guid as text)
    public const int Key = 128;       // login provider and token keys
    public const int ClaimValue = 512;
    public const int TokenValue = 1024;
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("AppUser");
        builder.HasKey(u => u.Id).IsClustered(false);
        builder.UseClusteredRowId("AppUser");

        builder.Property(u => u.UserName).HasMaxLength(IdentityLengths.Name);
        builder.Property(u => u.NormalizedUserName).HasMaxLength(IdentityLengths.Name);
        builder.Property(u => u.Email).HasMaxLength(IdentityLengths.Name);
        builder.Property(u => u.NormalizedEmail).HasMaxLength(IdentityLengths.Name);
        builder.Property(u => u.PasswordHash).HasMaxLength(256).IsUnicode(false); // base64 of the v3 hash (84 characters)
        builder.Property(u => u.SecurityStamp).HasMaxLength(IdentityLengths.Stamp).IsUnicode(false);
        builder.Property(u => u.ConcurrencyStamp).HasMaxLength(IdentityLengths.Stamp).IsUnicode(false);
        builder.Property(u => u.PhoneNumber).HasMaxLength(50);
    }
}

internal sealed class AppRoleConfiguration : IEntityTypeConfiguration<AppRole>
{
    // Fixed ids and stamps: the roles are part of the schema, and a migration must not rewrite them each time.
    private static readonly Dictionary<string, Guid> Ids = new(StringComparer.Ordinal)
    {
        [Roles.Clerk] = new("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1001"),
        [Roles.Approver] = new("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1002"),
        [Roles.Auditor] = new("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1003"),
        [Roles.Admin] = new("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1004"),
    };

    public void Configure(EntityTypeBuilder<AppRole> builder)
    {
        builder.ToTable("AppRole");
        builder.HasKey(r => r.Id).IsClustered(false);
        builder.UseClusteredRowId("AppRole");

        builder.Property(r => r.Name).HasMaxLength(IdentityLengths.Name);
        builder.Property(r => r.NormalizedName).HasMaxLength(IdentityLengths.Name);
        builder.Property(r => r.ConcurrencyStamp).HasMaxLength(IdentityLengths.Stamp).IsUnicode(false);

        builder.HasData(Roles.All.Select(name => new AppRole
        {
            Id = Ids[name],
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            ConcurrencyStamp = Ids[name].ToString("N"),
        }));
    }
}

internal sealed class AppUserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
        builder.ToTable("AppUserRole");
        builder.HasKey(r => new { r.UserId, r.RoleId }).IsClustered(false);
        builder.UseClusteredRowId("AppUserRole");
    }
}

internal sealed class AppUserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder)
    {
        builder.ToTable("AppUserClaim");
        builder.HasKey(c => c.Id).IsClustered(false);
        builder.Property(c => c.Id).UseSequence("AppUserClaimId"); // one IDENTITY per table, and that is RowId
        builder.UseClusteredRowId("AppUserClaim");
        builder.Property(c => c.ClaimType).HasMaxLength(IdentityLengths.Name);
        builder.Property(c => c.ClaimValue).HasMaxLength(IdentityLengths.ClaimValue);
    }
}

internal sealed class AppUserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder)
    {
        builder.ToTable("AppUserLogin");
        builder.HasKey(l => new { l.LoginProvider, l.ProviderKey }).IsClustered(false);
        builder.UseClusteredRowId("AppUserLogin");
        builder.Property(l => l.LoginProvider).HasMaxLength(IdentityLengths.Key);
        builder.Property(l => l.ProviderKey).HasMaxLength(IdentityLengths.Key);
        builder.Property(l => l.ProviderDisplayName).HasMaxLength(IdentityLengths.Name);
    }
}

internal sealed class AppUserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
    {
        builder.ToTable("AppUserToken");
        builder.HasKey(t => new { t.UserId, t.LoginProvider, t.Name }).IsClustered(false);
        builder.UseClusteredRowId("AppUserToken");
        builder.Property(t => t.LoginProvider).HasMaxLength(IdentityLengths.Key);
        builder.Property(t => t.Name).HasMaxLength(IdentityLengths.Key);
        builder.Property(t => t.Value).HasMaxLength(IdentityLengths.TokenValue);
    }
}

internal sealed class AppRoleClaimConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder)
    {
        builder.ToTable("AppRoleClaim");
        builder.HasKey(c => c.Id).IsClustered(false);
        builder.Property(c => c.Id).UseSequence("AppRoleClaimId"); // one IDENTITY per table, and that is RowId
        builder.UseClusteredRowId("AppRoleClaim");
        builder.Property(c => c.ClaimType).HasMaxLength(IdentityLengths.Name);
        builder.Property(c => c.ClaimValue).HasMaxLength(IdentityLengths.ClaimValue);
    }
}
