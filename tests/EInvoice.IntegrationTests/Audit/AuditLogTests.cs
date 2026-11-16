using System.Text.Json;
using EInvoice.Application.Audit;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Audit;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Audit;

/// <summary>
/// Package 11's "done when": editing an audit row by hand makes verification fail. The application cannot edit or
/// delete audit rows at all; only a database administrator could, and the verifier finds it. Tampering runs inside a
/// transaction that is rolled back, so the shared chain stays intact for every other test.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class AuditLogTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static Task<AuditVerification> VerifyAsync(EInvoiceDbContext db) =>
        AuditVerifier.VerifyAsync(new EfAuditLog(db).ReadInOrderAsync(CancellationToken.None), CancellationToken.None);

    /// <summary>Appends a few committed events and returns the sequence of one in the middle.</summary>
    private async Task<long> SomeCommittedEventsAsync()
    {
        long middle = 0;
        for (var i = 0; i < 3; i++)
        {
            await using var db = fixture.CreateContext();
            await new EfAuditLog(db).AppendAsync(new AuditDraft(Now, "tester@test", AuditActions.SignedIn, "User/tester", "{}"), CancellationToken.None);
            if (i == 1)
            {
                middle = await db.AuditEvents.MaxAsync(e => e.Sequence);
            }
        }

        return middle;
    }

    [Fact]
    public async Task Editing_an_audit_row_by_hand_makes_verification_fail()
    {
        var edited = await SomeCommittedEventsAsync();
        await using var admin = fixture.CreateAdminContext();
        await using var tx = await admin.Database.BeginTransactionAsync();

        await admin.Database.ExecuteSqlAsync($"UPDATE dbo.AuditEvent SET Details = N'{{\"forged\":true}}' WHERE Sequence = {edited}");
        var result = await VerifyAsync(admin);

        Assert.False(result.IsIntact);
        Assert.Equal((edited, AuditProblem.HashMismatch), (result.BrokenAt!.Value, result.Problem!.Value));

        await tx.RollbackAsync();
        await using var app = fixture.CreateContext();
        Assert.True((await VerifyAsync(app)).IsIntact); // and only because the edit was undone
    }

    [Fact]
    public async Task Deleting_an_audit_row_by_hand_makes_verification_fail()
    {
        var deleted = await SomeCommittedEventsAsync();
        await using var admin = fixture.CreateAdminContext();
        await using var tx = await admin.Database.BeginTransactionAsync();

        await admin.Database.ExecuteSqlAsync($"DELETE FROM dbo.AuditEvent WHERE Sequence = {deleted}");
        var result = await VerifyAsync(admin);

        Assert.Equal((deleted + 1, AuditProblem.SequenceGap), (result.BrokenAt!.Value, result.Problem!.Value));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task The_application_cannot_change_or_delete_audit_rows()
    {
        var sequence = await SomeCommittedEventsAsync();
        await using var app = fixture.CreateContext();

        var update = await Assert.ThrowsAsync<SqlException>(() => app.Database.ExecuteSqlAsync($"UPDATE dbo.AuditEvent SET Actor = N'x' WHERE Sequence = {sequence}"));
        var delete = await Assert.ThrowsAsync<SqlException>(() => app.Database.ExecuteSqlAsync($"DELETE FROM dbo.AuditEvent WHERE Sequence = {sequence}"));

        Assert.Equal((229, 229), (update.Number, delete.Number)); // permission denied
    }

    [Fact]
    public async Task A_status_change_is_audited_in_the_same_save()
    {
        await using var db = fixture.CreateContext();
        var price = 40m + (Random.Shared.Next(1, 100_000) / 100m);
        var scenario = await new MatchScenario(db).OrderLine(1, 1, price).Received(1, 1).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, price, 1)); // Received, then Parsed
        invoice.TransitionTo(InvoiceStatus.Exception, "approver@test", Now, "Checked by hand");
        await db.SaveChangesAsync();

        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Subject == $"Invoice/{invoice.Id}")
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        Assert.Equal(invoice.StatusHistory.Count, events.Count); // one event per status change, none missing
        var last = events[^1];
        Assert.Equal((AuditActions.InvoiceStatusChanged, "approver@test", Now), (last.Action, last.Actor, last.At));
        using var details = JsonDocument.Parse(last.Details);
        Assert.Equal("Parsed", details.RootElement.GetProperty("from").GetString());
        Assert.Equal("Exception", details.RootElement.GetProperty("to").GetString());
        Assert.Equal("Checked by hand", details.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_change_that_is_rolled_back_leaves_no_audit_event()
    {
        Guid invoiceId;
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var price = 50m + (Random.Shared.Next(1, 100_000) / 100m);
            var scenario = await new MatchScenario(db).OrderLine(1, 1, price).Received(1, 1).SaveOrderAsync();
            invoiceId = (await scenario.AddInvoiceAsync((1, 1, price, 1))).Id;
            await tx.RollbackAsync();
        }

        await using var check = fixture.CreateContext();
        Assert.False(await check.AuditEvents.AnyAsync(e => e.Subject == $"Invoice/{invoiceId}"));
        Assert.True((await VerifyAsync(check)).IsIntact); // no hole where the rolled-back events would have been
    }

    [Fact]
    public async Task Events_written_at_the_same_time_form_one_unbroken_chain()
    {
        var writers = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await using var db = fixture.CreateContext();
            await new EfAuditLog(db).AppendAsync(new AuditDraft(Now, $"parallel-{i}@test", AuditActions.SignedIn, $"User/parallel-{i}", "{}"), CancellationToken.None);
        }));

        await Task.WhenAll(writers);

        await using var check = fixture.CreateContext();
        var result = await VerifyAsync(check);
        Assert.True(result.IsIntact, $"broken at {result.BrokenAt}: {result.Problem}");
        Assert.Equal(await check.AuditEvents.CountAsync(), result.Count);
    }
}
