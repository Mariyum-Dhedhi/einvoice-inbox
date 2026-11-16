using EInvoice.Application.Queries;
using EInvoice.Domain.Invoices;
using EInvoice.Web.Components.Pages;

namespace EInvoice.ComponentTests;

/// <summary>
/// A ZUGFeRD invoice is labelled as such: read from the XML inside its PDF, not by AI. Words and an icon, never colour
/// alone; and it never carries the AI label.
/// </summary>
public sealed class ZugferdLabelTests : UiTest
{
    [Fact]
    public void Inbox_rows_mark_zugferd_invoices_and_not_as_ai()
    {
        Queries.Page = new InvoicePage(
            [Views.Summary(Guid.NewGuid(), number: "RE-ZF", format: SourceFormat.Zugferd), Views.Summary(Guid.NewGuid(), number: "RE-XML")], 1, 25, 2);

        var rows = Render<Inbox>().FindAll("[data-testid=invoice-row]");

        var badge = rows[0].QuerySelector("[data-testid=zugferd-badge]");
        Assert.NotNull(badge);
        Assert.Contains("ZUGFeRD", badge.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Empty(rows[0].QuerySelectorAll("[data-testid=ai-badge]"));
        Assert.Empty(rows[1].QuerySelectorAll("[data-testid=zugferd-badge]"));
    }

    [Fact]
    public void Detail_says_the_data_came_from_the_xml_inside_the_pdf()
    {
        var id = Guid.NewGuid();
        Queries.Details[id] = Views.Detail(id, InvoiceStatus.Matched, format: SourceFormat.Zugferd);

        var cut = Render<InvoiceDetailPage>(p => p.Add(c => c.Id, id));

        var chip = cut.Find("[data-testid=zugferd]");
        Assert.Contains("ZUGFeRD", chip.TextContent, StringComparison.Ordinal);
        Assert.Contains("XML in PDF", chip.TextContent, StringComparison.Ordinal);
        Assert.NotNull(chip.QuerySelector("svg"));
        Assert.Empty(cut.FindAll("[data-testid=ai-extracted]"));
    }
}
