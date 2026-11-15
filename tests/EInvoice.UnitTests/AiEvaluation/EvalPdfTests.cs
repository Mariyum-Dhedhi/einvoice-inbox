using System.Globalization;
using EInvoice.AiEvaluation;
using EInvoice.Application.Extraction;
using EInvoice.Infrastructure.Extraction;

namespace EInvoice.UnitTests.AiEvaluation;

/// <summary>
/// The committed evaluation PDFs are exactly what the case file describes: generated deterministically, never
/// edited by hand, and showing every true value in the layout's own notation. Hidden text is in the file but not visible.
/// </summary>
public class EvalPdfTests
{
    private static readonly IReadOnlyList<EvalCase> Cases = EvalCaseFile.Load(EvalCaseFile.DefaultPath(TestPaths.RepositoryRoot)).Cases;
    private static readonly PdfTextReader Reader = new(PdfLimits.Default);

    public static TheoryData<string> Ids() => new(Cases.Select(c => c.Id));

    private static EvalCase Case(string id) => Cases.Single(c => c.Id == id);

    [Fact]
    public void Rendering_is_deterministic()
    {
        Assert.All(Cases, c => Assert.Equal(EvalPdfRenderer.Render(c), EvalPdfRenderer.Render(c)));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Committed_pdf_is_exactly_what_the_case_file_describes(string id)
    {
        var path = EvalCaseFile.PdfPath(TestPaths.RepositoryRoot, id);

        Assert.True(System.IO.File.Exists(path), $"{id}.pdf is missing: run the generate command (see testdata/ai-eval/README.md).");
        Assert.True(EvalPdfRenderer.Render(Case(id)).AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(path)),
            $"{id}.pdf differs from cases.json: regenerate, never edit the PDF by hand.");
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_true_value_is_visible_in_the_layouts_notation(string id)
    {
        var c = Case(id);
        var text = Reader.Read(EvalPdfRenderer.Render(c)).Text!;
        var culture = c.Layout == "C" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("de-DE");

        Assert.Contains(c.InvoiceNumber, text, StringComparison.Ordinal);
        Assert.Contains(c.Truth.SellerName, text, StringComparison.Ordinal);
        Assert.Contains(c.Truth.GrossTotal.ToString("#,##0.00", culture), text, StringComparison.Ordinal);
        Assert.All(c.Truth.Lines, l => Assert.Contains(l.Description, text, StringComparison.Ordinal));
        if (c.PurchaseOrder is not null)
        {
            Assert.Contains(c.PurchaseOrder, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Layouts_really_differ()
    {
        string Text(string id) => Reader.Read(EvalPdfRenderer.Render(Case(id))).Text!;

        Assert.Contains("Invoice number", Text("E01"), StringComparison.Ordinal); // A: English labels
        Assert.Contains("Rechnungsnummer", Text("E02"), StringComparison.Ordinal); // B: German labels
        Assert.Contains("1,450.00", EvalPdfRenderer.RenderText(Case("E05") with { Layout = "C" }), StringComparison.Ordinal); // C: English notation
        Assert.Contains("1.450,00", Text("E05"), StringComparison.Ordinal); // B: German notation
        Assert.DoesNotContain("|", Text("E03"), StringComparison.Ordinal); // C: a positioned table, no separators
    }

    [Fact]
    public void Hidden_text_is_in_the_file_but_not_visible_and_only_where_intended()
    {
        foreach (var c in Cases)
        {
            var result = Reader.Read(EvalPdfRenderer.Render(c));
            Assert.Equal(c.HiddenText is not null, result.HiddenTextFound);
            if (c.HiddenText is not null)
            {
                Assert.DoesNotContain(c.HiddenText.Text, result.Text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Visible_injection_is_visible()
    {
        var c = Case("E27");

        Assert.Contains("ignore all previous instructions", Reader.Read(EvalPdfRenderer.Render(c)).Text, StringComparison.Ordinal);
    }
}
