using EInvoice.Application.Audit;
using EInvoice.Application.Vendors;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Validation;
using EInvoice.Infrastructure.Vendors;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.TestData;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Vendors;

/// <summary>
/// Bank accounts in SQL Server: the database keeps every earlier version itself (temporal table), refuses edits to that
/// history, validation trusts only confirmed accounts, every change is audited in the same transaction, and two admins
/// confirming at once cannot both succeed.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class BankAccountStoreTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A committed vendor of its own, so tests never share bank accounts.</summary>
    private async Task<Vendor> VendorAsync()
    {
        await using var db = fixture.CreateContext();
        var vendor = new Vendor($"T{Guid.NewGuid():N}"[..16], "Test Vendor GmbH", $"DE{Random.Shared.Next(100_000_000, 999_999_999)}");
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        return vendor;
    }

    private static Iban NewIban() => Iban.Parse(TestIbans.New());

    private BankAccountAdministration Administration(EInvoiceDbContext db) => new(new EfBankAccountStore(db), TimeProvider.System);

    private async Task<Guid> ProposeAsync(Guid vendorId, Iban iban, string admin = "admin@test")
    {
        await using var db = fixture.CreateContext();
        var result = await Administration(db).ProposeAsync(vendorId, iban.Value, admin, CancellationToken.None);
        Assert.Equal(BankAccountOutcome.Proposed, result.Outcome);
        return result.AccountId!.Value;
    }

    private async Task<BankAccountResult> ConfirmAsync(Guid accountId, string admin = "admin2@test")
    {
        await using var db = fixture.CreateContext();
        return await Administration(db).ConfirmAsync(accountId, admin, CancellationToken.None);
    }

    [Fact]
    public async Task Validation_trusts_an_account_only_after_the_second_person_confirmed_it()
    {
        var vendor = await VendorAsync();
        var iban = NewIban();
        var id = await ProposeAsync(vendor.Id, iban);

        await using (var db = fixture.CreateContext())
        {
            var pending = await new EfValidationDataSource(db).FindVendorByVatIdAsync(vendor.VatId!, CancellationToken.None);
            Assert.DoesNotContain(iban, pending!.ApprovedIbans); // proposed only: an invoice paying here still needs review
        }

        Assert.Equal(BankAccountOutcome.Confirmed, (await ConfirmAsync(id)).Outcome);

        await using (var db = fixture.CreateContext())
        {
            var confirmed = await new EfValidationDataSource(db).FindVendorByVatIdAsync(vendor.VatId!, CancellationToken.None);
            Assert.Contains(iban, confirmed!.ApprovedIbans);
        }
    }

    [Fact]
    public async Task The_database_keeps_every_version_with_its_time()
    {
        var vendor = await VendorAsync();
        var iban = NewIban();
        var id = await ProposeAsync(vendor.Id, iban);
        await ConfirmAsync(id);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(BankAccountOutcome.Removed, (await Administration(db).RemoveAsync(id, "admin@test", "Account closed.", CancellationToken.None)).Outcome);
        }

        await using var check = fixture.CreateContext();
        var history = (await new EfBankAccountQueries(check).VendorAsync(vendor.Id, CancellationToken.None))!.History;

        // Proposed (pending), then confirmed (active); the removal ends the last version.
        Assert.Equal(new[] { BankAccountStatus.Pending, BankAccountStatus.Active }, history.Where(h => h.AccountId == id).Select(h => h.Status));
        Assert.All(history.Where(h => h.AccountId == id), h => Assert.NotNull(h.ValidTo));
        Assert.Equal("admin2@test", history.Last(h => h.AccountId == id).ConfirmedBy);
        Assert.All(history, h => Assert.Equal(iban.ToString(), h.MaskedIban)); // history is for reading, the IBAN masked
        Assert.All(history, h => Assert.DoesNotContain(iban.Value[4..^4], h.MaskedIban, StringComparison.Ordinal));
        Assert.Empty((await new EfBankAccountQueries(check).VendorAsync(vendor.Id, CancellationToken.None))!.Accounts); // gone from today's list
    }

    [Fact]
    public async Task Nobody_can_rewrite_the_history_table()
    {
        var vendor = await VendorAsync();
        var id = await ProposeAsync(vendor.Id, NewIban());
        await ConfirmAsync(id);
        await using var admin = fixture.CreateAdminContext();
        await using var tx = await admin.Database.BeginTransactionAsync();

        // Even the database administrator gets an error while versioning is on: the history is system-maintained.
        var error = await Assert.ThrowsAsync<SqlException>(() => admin.Database.ExecuteSqlAsync($"UPDATE dbo.VendorBankAccountHistory SET ProposedBy = N'someone' WHERE Id = {id}"));

        Assert.Equal(13561, error.Number);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Every_change_is_audited_in_the_same_transaction()
    {
        var vendor = await VendorAsync();
        var id = await ProposeAsync(vendor.Id, NewIban());
        await ConfirmAsync(id);

        await using var db = fixture.CreateContext();
        var actions = await db.AuditEvents.AsNoTracking().Where(e => e.Subject == $"VendorBankAccount/{id}").OrderBy(e => e.Sequence)
            .Select(e => new { e.Action, e.Actor }).ToListAsync();

        Assert.Equal(new[] { (AuditActions.BankAccountProposed, "admin@test"), (AuditActions.BankAccountConfirmed, "admin2@test") }, actions.Select(a => (a.Action, a.Actor)));
        Assert.True((await new EInvoice.Infrastructure.Audit.EfAuditLog(db).VerifyAsync(CancellationToken.None)).IsIntact);
    }

    [Fact]
    public async Task Two_admins_confirming_at_once_cannot_both_succeed()
    {
        for (var round = 0; round < 3; round++)
        {
            var vendor = await VendorAsync();
            var id = await ProposeAsync(vendor.Id, NewIban());

            var results = await Task.WhenAll(
                Task.Run(() => ConfirmAsync(id, "admin2@test")),
                Task.Run(() => ConfirmAsync(id, "admin3@test")));

            Assert.Single(results, r => r.Outcome == BankAccountOutcome.Confirmed);
            Assert.Single(results, r => r.Outcome is BankAccountOutcome.Conflict or BankAccountOutcome.NotPending);
            await using var db = fixture.CreateContext();
            Assert.Single(await db.AuditEvents.Where(e => e.Subject == $"VendorBankAccount/{id}" && e.Action == AuditActions.BankAccountConfirmed).ToListAsync());
        }
    }

    [Fact]
    public async Task The_same_account_twice_for_one_vendor_is_refused_by_the_database_too()
    {
        // Two admins proposing the same IBAN at the same moment: the unique index decides, nobody gets an error page.
        var vendor = await VendorAsync();
        var iban = NewIban();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            await using var db = fixture.CreateContext();
            return await Administration(db).ProposeAsync(vendor.Id, iban.Value, $"admin{i}@test", CancellationToken.None);
        })));

        Assert.Single(results, r => r.Outcome == BankAccountOutcome.Proposed);
        Assert.Single(results, r => r.Outcome == BankAccountOutcome.AlreadyExists);
    }

    [Fact]
    public async Task Seeded_accounts_are_confirmed_ones()
    {
        await using var db = fixture.CreateContext();

        var seeded = await db.VendorBankAccounts.AsNoTracking().Where(a => a.ProposedBy == "seed").ToListAsync();

        Assert.NotEmpty(seeded);
        Assert.All(seeded, a => Assert.True(a.IsActive));
    }
}
