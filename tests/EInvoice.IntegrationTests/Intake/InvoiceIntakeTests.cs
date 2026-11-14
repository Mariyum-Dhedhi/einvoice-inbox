using System.Security.Cryptography;
using EInvoice.Application.Intake;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Infrastructure.Intake;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Validation;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using EInvoice.IntegrationTests.TestData;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Intake;

/// <summary>
/// The whole upload pipeline against a real database: checks, duplicate file, store original, parse, validate, match.
/// The KoSIT validator is replaced by a stand-in (its own tests run against the real container).
/// Each test runs in a transaction that is rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class InvoiceIntakeTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private const string Vendor1VatId = "DE000000001";
    private const string Vendor1Iban = "DE72000000000000001001";

    private EInvoiceDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = fixture.CreateContext();
        await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.RollbackTransactionAsync();
        await _db.DisposeAsync();
    }

    private sealed class StubConformance(ConformanceOutcome outcome) : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            Task.FromResult(outcome == ConformanceOutcome.Accepted ? ConformanceResult.Accepted : ConformanceResult.Unavailable);
    }

    private InvoiceIntake Intake(ConformanceOutcome conformance = ConformanceOutcome.Accepted, IntakeOptions? options = null)
    {
        var data = new EfValidationDataSource(_db);
        return new InvoiceIntake(
            new XRechnungParser(),
            new InvoiceValidator(data, new StubConformance(conformance)),
            data,
            new SqlInvoiceMatcher(_db),
            new EfIntakeStore(_db),
            MatchingPolicy.Default,
            options ?? IntakeOptions.Default,
            TimeProvider.System);
    }

    /// <summary>A fresh order for seeded vendor 1 (10 x 7.4125 and 4 x 39.90, fully received) and a matching invoice.</summary>
    private async Task<XRechnungBuilder> CleanInvoiceForNewOrderAsync()
    {
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 7.4125m).OrderLine(2, 4, 39.90m).Received(1, 10).Received(2, 4).SaveOrderAsync();
        return new XRechnungBuilder { PurchaseOrderNumber = scenario.PurchaseOrderNumber, SellerVatId = Vendor1VatId, Iban = Vendor1Iban }
            .Line("HDMI cable 2 m", 10m, 7.4125m, orderLine: "1")
            .Line("Handbook German commercial code", 4m, 39.90m, taxRate: 7m, orderLine: "2");
    }

    private static UploadedFile File(byte[] content, string name = "invoice.xml") => new(name, content);

    private Task<IntakeResult> SubmitAsync(byte[] content, ConformanceOutcome conformance = ConformanceOutcome.Accepted) =>
        Intake(conformance).SubmitAsync(File(content), "clerk@test", CancellationToken.None);

    // ---- Happy path ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Clean_invoice_goes_all_the_way_to_Matched()
    {
        var xml = (await CleanInvoiceForNewOrderAsync()).Build();

        var result = await SubmitAsync(xml);

        Assert.Equal(IntakeOutcome.Created, result.Outcome);
        Assert.Equal(InvoiceStatus.Matched, result.Status);
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        Assert.Equal(
            [InvoiceStatus.Received, InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched],
            invoice.StatusHistory.Select(h => h.To));
        Assert.Equal("clerk@test", invoice.SubmittedBy);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0001-000000000001"), invoice.VendorId);
        Assert.Equal([1, 2], invoice.Lines.Select(l => l.PurchaseOrderLineNumber!.Value));
        Assert.All(await _db.MatchResults.Where(r => r.InvoiceId == invoice.Id).ToListAsync(), r => Assert.Equal(MatchOutcome.FullMatch, r.Outcome));
        Assert.Empty(await _db.InvoiceFindings.Where(f => f.InvoiceId == invoice.Id).ToListAsync());
    }

    [Fact]
    public async Task Original_file_is_stored_byte_for_byte_with_its_hash()
    {
        var xml = (await CleanInvoiceForNewOrderAsync()).Build();

        var result = await Intake().SubmitAsync(File(xml, @"C:\fakepath\..\Rechnung <1>.xml"), "clerk@test", CancellationToken.None);

        var document = await _db.InvoiceDocuments.SingleAsync(d => d.InvoiceId == result.InvoiceId);
        Assert.Equal(xml, document.Content);
        Assert.Equal(SHA256.HashData(xml), document.Sha256);
        Assert.Equal(xml.Length, document.SizeBytes);
        Assert.Equal("application/xml", document.ContentType);
        Assert.Equal("Rechnung _1_.xml", document.FileName);
    }

    // ---- Exceptions -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Price_above_tolerance_ends_in_Exception_with_the_match_reason()
    {
        var builder = await CleanInvoiceForNewOrderAsync();
        var xml = new XRechnungBuilder { PurchaseOrderNumber = builder.PurchaseOrderNumber, SellerVatId = Vendor1VatId, Iban = Vendor1Iban }
            .Line("HDMI cable 2 m", 10m, 7.79m, orderLine: "1") // +5%
            .Build();

        var result = await SubmitAsync(xml);

        Assert.Equal(InvoiceStatus.Exception, result.Status);
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        Assert.Equal("PriceVariance", invoice.StatusHistory[^1].Reason);
    }

    [Fact]
    public async Task Unknown_vendor_ends_in_Exception_with_a_blocking_finding_and_no_match()
    {
        // Official KoSIT sample: seller VAT id DE 123456789 is not one of our vendors.
        var xml = await System.IO.File.ReadAllBytesAsync(Path.Combine(SqlServerFixture.RepositoryRoot(), "testdata", "xrechnung", "kosit", "01.01a-INVOICE_ubl.xml"));

        var result = await SubmitAsync(xml);

        Assert.Equal(InvoiceStatus.Exception, result.Status);
        var findings = await _db.InvoiceFindings.Where(f => f.InvoiceId == result.InvoiceId).ToListAsync();
        var vendor = Assert.Single(findings, f => f.RuleCode == RuleCodes.VendorUnknown);
        Assert.Equal(FindingSeverity.Blocking, vendor.Severity);
        Assert.Empty(await _db.MatchResults.Where(r => r.InvoiceId == result.InvoiceId).ToListAsync());
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        Assert.Contains(RuleCodes.VendorUnknown, invoice.StatusHistory[^1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validator_down_sends_the_invoice_to_a_human()
    {
        var xml = (await CleanInvoiceForNewOrderAsync()).Build();

        var result = await SubmitAsync(xml, ConformanceOutcome.Unavailable);

        Assert.Equal(InvoiceStatus.Exception, result.Status);
        Assert.Contains(await _db.InvoiceFindings.Where(f => f.InvoiceId == result.InvoiceId).ToListAsync(),
            f => f.RuleCode == RuleCodes.ConformanceUnavailable);
    }

    // ---- Unreadable uploads are kept as Failed -----------------------------------------------------------------

    [Theory]
    [InlineData("xxe-local-file.xml")]
    [InlineData("billion-laughs.xml")]
    [InlineData("malformed.xml")]
    [InlineData("credit-note.xml")]
    public async Task Unreadable_xml_is_stored_as_Failed_with_the_reason(string fileName)
    {
        var xml = await System.IO.File.ReadAllBytesAsync(Path.Combine(SqlServerFixture.RepositoryRoot(), "testdata", "xrechnung", "malicious", fileName));

        var result = await SubmitAsync(xml);

        Assert.Equal(IntakeOutcome.Created, result.Outcome);
        Assert.Equal(InvoiceStatus.Failed, result.Status);
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        Assert.Equal([InvoiceStatus.Received, InvoiceStatus.Failed], invoice.StatusHistory.Select(h => h.To));
        Assert.False(string.IsNullOrWhiteSpace(invoice.StatusHistory[^1].Reason));
        Assert.Null(invoice.InvoiceNumber);
        Assert.True(await _db.InvoiceDocuments.AnyAsync(d => d.InvoiceId == result.InvoiceId)); // kept for audit, never executed
    }

    [Fact]
    public async Task Values_longer_than_the_database_allows_are_Failed_not_a_server_error()
    {
        var xml = new XRechnungBuilder { InvoiceNumber = new string('9', 60), PurchaseOrderNumber = "PO-0001" }.Line("Item", 1m, 10m).Build();

        var result = await SubmitAsync(xml);

        Assert.Equal(InvoiceStatus.Failed, result.Status);
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        Assert.Contains("BT-1", invoice.StatusHistory[^1].Reason, StringComparison.Ordinal);
    }

    // ---- Refused before anything is stored ---------------------------------------------------------------------

    [Fact]
    public async Task Same_file_twice_is_a_duplicate_file_pointing_at_the_first_invoice()
    {
        var xml = (await CleanInvoiceForNewOrderAsync()).Build();
        var first = await SubmitAsync(xml);
        var count = await _db.Invoices.CountAsync();

        var second = await SubmitAsync(xml);

        Assert.Equal(IntakeOutcome.DuplicateFile, second.Outcome);
        Assert.Equal(first.InvoiceId, second.InvoiceId);
        Assert.Equal(count, await _db.Invoices.CountAsync());
    }

    [Fact]
    public async Task Different_file_with_the_same_vendor_and_number_is_a_duplicate_invoice_number()
    {
        var builder = await CleanInvoiceForNewOrderAsync();
        await SubmitAsync(builder.Build());
        builder.IssueDate = builder.IssueDate.AddDays(1); // different bytes, same vendor + number
        var count = await _db.Invoices.CountAsync();

        var second = await SubmitAsync(builder.Build());

        Assert.Equal(IntakeOutcome.DuplicateInvoiceNumber, second.Outcome);
        Assert.Null(second.InvoiceId);
        Assert.Equal(count, await _db.Invoices.CountAsync());
    }

    [Fact]
    public async Task Empty_file_is_refused()
    {
        Assert.Equal(IntakeOutcome.Empty, (await SubmitAsync([])).Outcome);
    }

    [Fact]
    public async Task File_over_the_limit_is_refused()
    {
        var result = await Intake(options: new IntakeOptions(MaxDocumentBytes: 100)).SubmitAsync(File(new byte[101]), "clerk@test", CancellationToken.None);

        Assert.Equal(IntakeOutcome.TooLarge, result.Outcome);
    }

    [Fact]
    public async Task Pdf_is_not_supported_yet_and_nothing_is_stored()
    {
        var count = await _db.Invoices.CountAsync();

        var result = await SubmitAsync("%PDF-1.7\n"u8.ToArray());

        Assert.Equal(IntakeOutcome.UnsupportedType, result.Outcome);
        Assert.Equal(count, await _db.Invoices.CountAsync());
    }

    [Fact]
    public async Task Png_renamed_to_xml_is_refused_because_the_type_comes_from_the_content()
    {
        var result = await Intake().SubmitAsync(File([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "invoice.xml"), "clerk@test", CancellationToken.None);

        Assert.Equal(IntakeOutcome.UnsupportedType, result.Outcome);
    }
}
