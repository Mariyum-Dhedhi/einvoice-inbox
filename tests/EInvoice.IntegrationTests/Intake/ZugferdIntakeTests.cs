using EInvoice.Application.Extraction;
using EInvoice.Application.Intake;
using EInvoice.Domain.Matching;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
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
/// ZUGFeRD / Factur-X PDFs through the whole pipeline: the embedded XML is the invoice. It is read by the parser, never
/// by AI; checked by KoSIT only in the XRECHNUNG profile; and goes through the same rules and matching as XML.
/// The original PDF is kept. Each test is rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class ZugferdIntakeTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private EInvoiceDbContext _db = null!;
    private readonly RecordingConformance _kosit = new();

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

    private sealed class RecordingConformance : IConformanceValidator
    {
        public List<byte[]> Checked { get; } = [];

        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken)
        {
            Checked.Add(xml.ToArray());
            return Task.FromResult(ConformanceResult.Accepted);
        }
    }

    /// <summary>A ZUGFeRD invoice is never sent to the model: its data is already structured.</summary>
    private sealed class NoAi : IInvoiceExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("AI extraction was asked about a ZUGFeRD invoice.");
    }

    private InvoiceIntake Intake()
    {
        var data = new EfValidationDataSource(_db);
        return new InvoiceIntake(
            new XRechnungParser(),
            new InvoiceValidator(data, _kosit),
            data,
            new SqlInvoiceMatcher(_db),
            new EfIntakeStore(_db),
            MatchingPolicy.Default,
            IntakeOptions.Default,
            TimeProvider.System,
            new NoAi(),
            new PdfEmbeddedInvoiceReader(PdfLimits.Default, IntakeOptions.Default.MaxDocumentBytes));
    }

    /// <summary>A fresh order for seeded vendor 1 (10 x a unique price, received) and a CII invoice for it.</summary>
    private async Task<CiiInvoiceBuilder> InvoiceForNewOrderAsync(string profile)
    {
        var price = 3m + (Random.Shared.Next(1, 100_000) / 10_000m);
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, price).Received(1, 10).SaveOrderAsync();
        return new CiiInvoiceBuilder { Profile = profile, PurchaseOrderNumber = scenario.PurchaseOrderNumber }
            .Line("HDMI cable 2 m", 10m, price, orderLine: "1");
    }

    private Task<IntakeResult> SubmitAsync(byte[] pdf) =>
        Intake().SubmitAsync(new UploadedFile("Rechnung.pdf", pdf), "clerk@test", CancellationToken.None);

    private Task<List<InvoiceFindingRecord>> FindingsAsync(Guid? id) => _db.InvoiceFindings.Where(f => f.InvoiceId == id).ToListAsync();

    [Fact]
    public async Task An_xrechnung_profile_invoice_is_checked_by_kosit_and_matched()
    {
        var builder = await InvoiceForNewOrderAsync(CiiInvoiceBuilder.Profiles.XRechnung);
        var xml = builder.Build();
        var pdf = ZugferdPdf.Invoice(xml, "xrechnung.xml");

        var result = await SubmitAsync(pdf);

        Assert.Equal((IntakeOutcome.Created, InvoiceStatus.Matched), (result.Outcome, result.Status!.Value));
        Assert.Equal(xml, Assert.Single(_kosit.Checked)); // the embedded XML, not the PDF
        Assert.Empty(await FindingsAsync(result.InvoiceId));
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        Assert.Equal((SourceFormat.Zugferd, builder.InvoiceNumber), (invoice.SourceFormat, invoice.InvoiceNumber));
        var document = await _db.InvoiceDocuments.SingleAsync(d => d.InvoiceId == result.InvoiceId);
        Assert.Equal(("application/pdf", pdf), (document.ContentType, document.Content)); // the original is kept as sent
    }

    [Theory]
    [InlineData(CiiInvoiceBuilder.Profiles.Basic)]
    [InlineData(CiiInvoiceBuilder.Profiles.En16931)]
    [InlineData(CiiInvoiceBuilder.Profiles.Extended)]
    public async Task Other_profiles_get_every_document_rule_and_a_note_that_kosit_did_not_check_them(string profile)
    {
        var pdf = ZugferdPdf.Invoice((await InvoiceForNewOrderAsync(profile)).Build());

        var result = await SubmitAsync(pdf);

        Assert.Equal((IntakeOutcome.Created, InvoiceStatus.Exception), (result.Outcome, result.Status!.Value));
        Assert.Empty(_kosit.Checked); // KoSIT knows XRechnung only: it would wrongly call these not conformant
        var finding = Assert.Single(await FindingsAsync(result.InvoiceId));
        Assert.Equal((RuleCodes.ConformanceNotChecked, FindingSeverity.Review), (finding.RuleCode, finding.Severity));
        Assert.Equal(SourceFormat.Zugferd, (await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId)).SourceFormat);
    }

    [Fact]
    public async Task The_document_rules_still_run_on_the_embedded_xml()
    {
        // Paid to an account the vendor never had: the same finding as for an XML upload.
        var builder = await InvoiceForNewOrderAsync(CiiInvoiceBuilder.Profiles.En16931);
        builder.Iban = "DE89370400440532013000";

        var result = await SubmitAsync(ZugferdPdf.Invoice(builder.Build()));

        Assert.Contains(await FindingsAsync(result.InvoiceId), f => f.RuleCode == RuleCodes.IbanNotApproved);
    }

    [Theory]
    [InlineData(CiiInvoiceBuilder.Profiles.Minimum, "MINIMUM")]
    [InlineData(CiiInvoiceBuilder.Profiles.BasicWl, "BASIC WL")]
    [InlineData("urn:example:unknown-profile", "not supported")]
    public async Task Profiles_without_invoice_lines_are_refused_and_nothing_is_stored(string profile, string named)
    {
        var before = await _db.Invoices.CountAsync();

        var result = await SubmitAsync(ZugferdPdf.Invoice((await InvoiceForNewOrderAsync(profile)).Build()));

        Assert.Equal(IntakeOutcome.UnsupportedType, result.Outcome);
        Assert.Contains(named, result.Detail, StringComparison.Ordinal);
        Assert.Equal(before, await _db.Invoices.CountAsync());
    }

    [Fact]
    public async Task An_unreadable_embedded_xml_is_kept_as_failed_with_the_reason()
    {
        var pdf = ZugferdPdf.Invoice("<rsm:CrossIndustryInvoice><unclosed>"u8.ToArray());

        var result = await SubmitAsync(pdf);

        Assert.Equal((IntakeOutcome.Created, InvoiceStatus.Failed), (result.Outcome, result.Status!.Value));
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        Assert.Equal(SourceFormat.Zugferd, invoice.SourceFormat);
        Assert.Contains("embedded", invoice.StatusHistory[^1].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_embedded_invoices_are_kept_as_failed_neither_is_chosen()
    {
        var xml = (await InvoiceForNewOrderAsync(CiiInvoiceBuilder.Profiles.En16931)).Build();
        var pdf = ZugferdPdf.Build("Rechnung", ("factur-x.xml", xml), ("zugferd-invoice.xml", xml));

        var result = await SubmitAsync(pdf);

        Assert.Equal(InvoiceStatus.Failed, result.Status);
        Assert.Contains("more than one", (await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId)).StatusHistory[^1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_pdf_twice_is_a_duplicate()
    {
        var pdf = ZugferdPdf.Invoice((await InvoiceForNewOrderAsync(CiiInvoiceBuilder.Profiles.XRechnung)).Build(), "xrechnung.xml");
        var first = await SubmitAsync(pdf);

        var second = await SubmitAsync(pdf);

        Assert.Equal((IntakeOutcome.DuplicateFile, first.InvoiceId), (second.Outcome, second.InvoiceId));
    }
}
