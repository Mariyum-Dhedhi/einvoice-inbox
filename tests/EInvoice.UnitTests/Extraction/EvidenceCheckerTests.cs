using EInvoice.Application.Extraction;
using EInvoice.Application.Parsing;

namespace EInvoice.UnitTests.Extraction;

/// <summary>
/// Confidence is computed, not asked for: a field is confident only when its value can be found in the visible
/// text of the PDF and the arithmetic around it agrees. What the model says about its own certainty plays no part.
/// </summary>
public class EvidenceCheckerTests
{
    private const string Iban = "DE72000000000000001001";

    private static readonly string Text = string.Join('\n', new SyntheticPdf()
        .Line("HDMI cable 2 m", 10m, 7.41m, orderLine: "1")
        .Line("Handbook", 4m, 39.90m, rate: 7m)
        .VisibleLines());

    private static ExtractedInvoice Invoice(Func<ExtractedInvoice, ExtractedInvoice>? change = null)
    {
        var invoice = new ExtractedInvoice(
            "RE-2026-1001", "2026-11-02", "EUR", "PO-0042", "Beispiel Buerobedarf GmbH", "DE000000001", [Iban],
            233.70m, 25.25m, 258.95m,
            [
                new ExtractedLine("HDMI cable 2 m", 10m, 7.41m, 74.10m, 19m, "1"),
                new ExtractedLine("Handbook", 4m, 39.90m, 159.60m, 7m, null),
            ]);
        return change?.Invoke(invoice) ?? invoice;
    }

    private static IReadOnlyList<FieldConfidence> Check(ExtractedInvoice invoice) => EvidenceChecker.Check(invoice, Text, [Iban]);

    private static FieldConfidence Field(IReadOnlyList<FieldConfidence> fields, string term, int? line = null) =>
        Assert.Single(fields, f => f.Term == term && f.LinePosition == line);

    [Fact]
    public void Every_field_of_a_faithful_extraction_is_confident()
    {
        var fields = Check(Invoice());

        Assert.All(fields, f => Assert.True(f.Confident, $"{f.Term} line {f.LinePosition}: {f.Reason}"));
        // Header terms once, line terms once per line.
        Assert.Equal(
            ["BT-1", "BT-109", "BT-110", "BT-112", "BT-13", "BT-2", "BT-27", "BT-31", "BT-5", "BT-84"],
            fields.Where(f => f.LinePosition is null).Select(f => f.Term).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["BT-129", "BT-131", "BT-132", "BT-146", "BT-152", "BT-153"],
            fields.Where(f => f.LinePosition == 1).Select(f => f.Term).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("RE-2026-9999")] // invented
    [InlineData("RE-2026-100")]  // cut short: must be the whole token, not a part of one
    public void Invoice_number_that_is_not_in_the_document_is_not_confident(string number)
    {
        var field = Field(Check(Invoice(i => i with { InvoiceNumber = number })), "BT-1");

        Assert.False(field.Confident);
        Assert.NotNull(field.Reason);
        Assert.DoesNotContain(number, field.Reason, StringComparison.Ordinal); // reasons never repeat model output
    }

    [Fact]
    public void Text_fields_match_regardless_of_case_and_spacing()
    {
        var fields = Check(Invoice(i => i with { SellerName = "BEISPIEL  Buerobedarf   gmbh", SellerVatId = "DE 000 000 001" }));

        Assert.True(Field(fields, "BT-27").Confident);
        Assert.True(Field(fields, "BT-31").Confident);
    }

    [Theory]
    [InlineData("2026-11-02", true)]  // printed as 02.11.2026
    [InlineData("2026-02-11", false)] // day and month swapped
    [InlineData("2026-11-03", false)]
    [InlineData("02.11.2026", false)] // not the format that was asked for
    public void Date_is_confident_only_when_that_day_is_printed(string date, bool confident)
    {
        Assert.Equal(confident, Field(Check(Invoice(i => i with { IssueDate = date })), "BT-2").Confident);
    }

    [Fact]
    public void Dates_are_found_in_the_usual_written_forms()
    {
        foreach (var printed in new[] { "02.11.2026", "2.11.2026", "2026-11-02", "2. November 2026", "02/11/2026" })
        {
            var fields = EvidenceChecker.Check(Invoice(), Text.Replace("02.11.2026", printed, StringComparison.Ordinal), [Iban]);
            Assert.True(Field(fields, "BT-2").Confident, printed);
        }
    }

    [Fact]
    public void Amounts_are_found_in_German_and_English_notation()
    {
        var invoice = Invoice(i => i with { GrossTotal = 1234.56m, NetTotal = 1037.44m, TaxTotal = 197.12m });
        foreach (var printed in new[] { "1.234,56", "1234,56", "1,234.56", "1234.56" })
        {
            var text = Text.Replace("258,95", printed, StringComparison.Ordinal).Replace("233,70", "1.037,44", StringComparison.Ordinal).Replace("25,25", "197,12", StringComparison.Ordinal);
            Assert.True(Field(EvidenceChecker.Check(invoice, text, [Iban]), "BT-112").Confident, printed);
        }
    }

    [Fact]
    public void Amount_must_be_a_whole_number_in_the_text_not_part_of_a_longer_one()
    {
        // 58.95 is "in" 258,95 as characters, but it is not a number the document states.
        var fields = Check(Invoice(i => i with { GrossTotal = 58.95m }));

        Assert.False(Field(fields, "BT-112").Confident);
    }

    [Fact]
    public void Total_that_is_printed_but_does_not_add_up_is_not_confident()
    {
        // All three totals are printed, but net + VAT is not the gross: the model may have mixed up numbers.
        var text = Text.Replace("258,95", "268,95", StringComparison.Ordinal);
        var fields = EvidenceChecker.Check(Invoice(i => i with { GrossTotal = 268.95m }), text, [Iban]);

        Assert.False(Field(fields, "BT-112").Confident);
        Assert.Contains("add up", Field(fields, "BT-112").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Net_total_must_equal_the_sum_of_the_lines()
    {
        var text = Text.Replace("Net total: 233,70", "Net total: 233,70 (was 240,00)", StringComparison.Ordinal);
        var fields = EvidenceChecker.Check(Invoice(i => i with { NetTotal = 240.00m }), text, [Iban]);

        Assert.False(Field(fields, "BT-109").Confident);
    }

    [Fact]
    public void Line_amount_must_be_printed_and_equal_quantity_times_price()
    {
        var fields = Check(Invoice(i => i with { Lines = [i.Lines![0] with { Quantity = 4m }, i.Lines[1]] })); // 4 is printed (line 2), but 4 x 7.41 is not 74.10

        Assert.False(Field(fields, "BT-131", 1).Confident);
        Assert.True(Field(fields, "BT-131", 2).Confident);
    }

    [Fact]
    public void Line_description_and_price_must_come_from_the_document()
    {
        var fields = Check(Invoice(i => i with { Lines = [i.Lines![0] with { Description = "Gold bar 1 kg", UnitPrice = 7.99m }, i.Lines[1]] }));

        Assert.False(Field(fields, "BT-153", 1).Confident);
        Assert.False(Field(fields, "BT-146", 1).Confident);
    }

    [Fact]
    public void Iban_is_confident_only_if_it_was_masked_out_of_this_document()
    {
        var fields = EvidenceChecker.Check(Invoice(i => i with { Ibans = ["DE89370400440532013000"] }), Text, [Iban]);

        Assert.False(Field(fields, "BT-84").Confident);
    }

    [Fact]
    public void Missing_optional_values_are_confident_when_the_document_has_none_either()
    {
        var text = Text.Replace("Purchase order: PO-0042\n", string.Empty, StringComparison.Ordinal);
        var fields = EvidenceChecker.Check(Invoice(i => i with { PurchaseOrderReference = null }), text, [Iban]);

        Assert.True(Field(fields, "BT-13").Confident);
    }

    [Fact]
    public void Leaving_out_a_value_the_document_has_is_not_confident()
    {
        // The document prints "Purchase order: PO-0042" and an IBAN; a reply without them dropped something.
        var fields = Check(Invoice(i => i with { PurchaseOrderReference = null, Ibans = [] }));

        Assert.False(Field(fields, "BT-13").Confident);
        Assert.False(Field(fields, "BT-84").Confident);
    }

    [Fact]
    public void Currency_is_found_as_code_or_symbol()
    {
        Assert.True(Field(Check(Invoice()), "BT-5").Confident);
        Assert.True(Field(EvidenceChecker.Check(Invoice(), Text.Replace(" EUR", " €", StringComparison.Ordinal), [Iban]), "BT-5").Confident);
        Assert.False(Field(Check(Invoice(i => i with { Currency = "USD" })), "BT-5").Confident);
    }
}

/// <summary>The checked extraction becomes the same <see cref="ParsedDocument"/> the XML parsers produce, so one set of rules validates both.</summary>
public class ExtractedInvoiceMapperTests
{
    private static ExtractedInvoice Invoice() => new(
        "RE-2026-1001", "2026-11-02", "EUR", "PO-0042", "Beispiel Buerobedarf GmbH", "DE 000 000 001", ["DE72000000000000001001"],
        233.70m, 25.25m, 258.95m,
        [
            new ExtractedLine("HDMI cable 2 m", 10m, 7.41m, 74.10m, 19m, "1"),
            new ExtractedLine("Handbook", 4m, 39.90m, 159.60m, 7m, null),
            new ExtractedLine("Notebook", 1m, 10.00m, 10.00m, 19m, null),
        ]);

    [Fact]
    public void Maps_to_the_pipeline_document()
    {
        Assert.True(ExtractedInvoiceMapper.TryMap(Invoice(), out var document, out var error), error?.Message);

        Assert.Equal(DocumentSyntax.Pdf, document!.Syntax);
        Assert.Equal("380", document.InvoiceTypeCode);
        Assert.Equal("RE-2026-1001", document.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 2), document.IssueDate);
        Assert.Equal("DE000000001", document.SellerVatId); // spaces removed, like the XML parsers do
        Assert.Equal([1, 2, 3], document.Lines.Select(l => l.Position));
        Assert.Equal("1", document.Lines[0].PurchaseOrderLineReference);
    }

    [Fact]
    public void Totals_for_the_rules_are_recomputed_not_taken_from_the_model()
    {
        ExtractedInvoiceMapper.TryMap(Invoice() with { TaxTotal = 99.99m }, out var document, out _);

        // The stated total stays as stated (the TOTALS and VAT_BREAKDOWN rules compare against it) ...
        Assert.Equal(99.99m, document!.TaxTotal);
        // ... while the breakdown is computed from the lines: 84.10 at 19 % and 159.60 at 7 %.
        Assert.Equal(
            [(19m, 84.10m, 15.98m), (7m, 159.60m, 11.17m)],
            document.VatBreakdown.Select(v => (v.RatePercent, v.TaxableAmount, v.TaxAmount)));
        Assert.Equal(233.70m, document.SumOfLineNetAmounts); // the stated net total, compared with the lines by LINE_SUM
    }

    [Theory]
    [InlineData("number")]
    [InlineData("date")]
    [InlineData("currency")]
    [InlineData("seller")]
    [InlineData("gross")]
    [InlineData("lines")]
    [InlineData("line-quantity")]
    public void Missing_required_value_fails_and_names_the_business_term(string missing)
    {
        var invoice = missing switch
        {
            "number" => Invoice() with { InvoiceNumber = null },
            "date" => Invoice() with { IssueDate = null },
            "currency" => Invoice() with { Currency = null },
            "seller" => Invoice() with { SellerName = " " },
            "gross" => Invoice() with { GrossTotal = null },
            "lines" => Invoice() with { Lines = [] },
            _ => Invoice() with { Lines = [Invoice().Lines![0] with { Quantity = null }] },
        };

        Assert.False(ExtractedInvoiceMapper.TryMap(invoice, out var document, out var error));

        Assert.Null(document);
        Assert.Equal(ExtractionErrorCode.MissingField, error!.Code);
        Assert.Contains("BT-", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-13-40")]
    [InlineData("02.11.2026")]
    [InlineData("yesterday")]
    public void Date_that_is_not_an_iso_date_fails_without_repeating_it(string date)
    {
        Assert.False(ExtractedInvoiceMapper.TryMap(Invoice() with { IssueDate = date }, out _, out var error));

        Assert.Equal(ExtractionErrorCode.InvalidModelOutput, error!.Code);
        Assert.DoesNotContain(date, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("eu")]
    [InlineData("€")]
    public void Currency_must_be_a_three_letter_code(string currency)
    {
        Assert.False(ExtractedInvoiceMapper.TryMap(Invoice() with { Currency = currency }, out _, out var error));
        Assert.Equal(ExtractionErrorCode.InvalidModelOutput, error!.Code);
    }
}
