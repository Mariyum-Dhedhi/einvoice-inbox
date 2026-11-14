using System.Text;
using EInvoice.Application.Intake;
using EInvoice.Application.Queries;
using EInvoice.Domain.Invoices;
using EInvoice.Web.Components.Pages;
using Microsoft.AspNetCore.Components.Forms;

namespace EInvoice.ComponentTests;

/// <summary>
/// The inbox: a list that can be filtered and paged through links (so every view has a URL), and an upload that
/// opens the new invoice or explains a refusal.
/// </summary>
public sealed class InboxTests : UiTest
{
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes("<Invoice/>");

    private IRenderedComponent<Inbox> RenderAt(string uri)
    {
        NavigateTo(uri);
        return Render<Inbox>();
    }

    // ---- List ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Rows_show_status_number_vendor_date_amount_and_link_to_the_detail()
    {
        var id = Guid.NewGuid();
        Queries.Page = new InvoicePage([Views.Summary(id, InvoiceStatus.Exception)], 1, 25, 1);

        var cut = RenderAt("/");

        var row = cut.Find("[data-testid=invoice-row]");
        Assert.Contains("Exception", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("RE-2026-0001", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Beispiel Bürobedarf GmbH", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("02.11.2026", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("1.234,56 €", row.TextContent, StringComparison.Ordinal);
        Assert.Contains(row.QuerySelectorAll("a"), a => a.GetAttribute("href") == $"/invoices/{id}");
    }

    [Fact]
    public void Invoice_without_number_or_vendor_says_so_instead_of_an_empty_cell()
    {
        Queries.Page = new InvoicePage([Views.Summary(Guid.NewGuid(), InvoiceStatus.Failed, number: null, vendor: null)], 1, 25, 1);

        var row = RenderAt("/").Find("[data-testid=invoice-row]");

        Assert.Contains("Not readable", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Unknown vendor", row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_inbox_explains_what_to_do()
    {
        var cut = RenderAt("/");

        Assert.Contains("Upload", cut.Find("[data-testid=empty]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=invoice-row]"));
    }

    [Fact]
    public void Filter_and_page_come_from_the_address()
    {
        RenderAt("/?status=Exception&page=2");

        var call = Assert.Single(Queries.ListCalls);
        Assert.Equal(new InvoiceListQuery(InvoiceStatus.Exception, 2, InvoiceListQuery.DefaultPageSize), call);
    }

    [Theory]
    [InlineData("/?status=Bogus&page=-3")]
    [InlineData("/?status=99&page=abc")]
    public void Invalid_address_values_fall_back_to_all_and_page_one(string uri)
    {
        RenderAt(uri);

        Assert.Equal(new InvoiceListQuery(null, 1, InvoiceListQuery.DefaultPageSize), Assert.Single(Queries.ListCalls));
    }

    [Fact]
    public void Status_filters_are_links_and_the_current_one_is_marked()
    {
        var cut = RenderAt("/?status=Exception&page=3");

        var exception = cut.Find("[data-testid=filter-Exception]");
        Assert.Equal("/?status=Exception", exception.GetAttribute("href")); // a new filter starts at page 1
        Assert.Equal("page", exception.GetAttribute("aria-current"));
        Assert.Equal("/", cut.Find("[data-testid=filter-all]").GetAttribute("href"));
        Assert.Null(cut.Find("[data-testid=filter-all]").GetAttribute("aria-current"));
    }

    [Fact]
    public void Paging_shows_the_range_and_links_keep_the_filter()
    {
        Queries.Page = new InvoicePage(Enumerable.Range(0, 25).Select(_ => Views.Summary(Guid.NewGuid())).ToList(), 2, 25, 120);

        var cut = RenderAt("/?status=Matched&page=2");

        Assert.Contains("26–50 of 120", cut.Find("[data-testid=page-range]").TextContent, StringComparison.Ordinal);
        Assert.Equal("/?status=Matched&page=3", cut.Find("[data-testid=page-next]").GetAttribute("href"));
        Assert.Equal("/?status=Matched", cut.Find("[data-testid=page-prev]").GetAttribute("href")); // page 1 has one address
    }

    [Fact]
    public void No_previous_link_on_the_first_page_and_no_next_link_on_the_last()
    {
        Queries.Page = new InvoicePage([Views.Summary(Guid.NewGuid())], 1, 25, 1);

        var cut = RenderAt("/");

        Assert.Empty(cut.FindAll("[data-testid=page-prev]"));
        Assert.Empty(cut.FindAll("[data-testid=page-next]"));
    }

    [Fact]
    public void Text_from_the_document_is_shown_as_text_never_as_markup()
    {
        Queries.Page = new InvoicePage([Views.Summary(Guid.NewGuid(), number: "<script>alert(1)</script>", vendor: "<img src=x onerror=alert(2)>")], 1, 25, 1);

        var cut = RenderAt("/");

        Assert.Empty(cut.FindAll("[data-testid=invoice-row] script"));
        Assert.Empty(cut.FindAll("[data-testid=invoice-row] img"));
        Assert.Contains("<script>alert(1)</script>", cut.Find("[data-testid=invoice-row]").TextContent, StringComparison.Ordinal);
    }

    // ---- Upload -------------------------------------------------------------------------------------------------

    [Fact]
    public void Upload_sends_name_bytes_and_user_and_opens_the_new_invoice()
    {
        var id = Guid.NewGuid();
        Intake.Result = new IntakeResult(IntakeOutcome.Created, id, InvoiceStatus.Matched);
        var cut = RenderAt("/");

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Xml, "Rechnung 1.xml"));

        cut.WaitForAssertion(() => Assert.EndsWith($"/invoices/{id}", CurrentUri, StringComparison.Ordinal));
        var (file, user) = Assert.Single(Intake.Calls);
        Assert.Equal("Rechnung 1.xml", file.FileName);
        Assert.Equal(Xml, file.Content);
        Assert.Equal("clerk@test", user);
    }

    [Fact]
    public void Refused_upload_shows_the_reason_and_stays_on_the_inbox()
    {
        Intake.Result = new IntakeResult(IntakeOutcome.UnsupportedType, Detail: "Only XRechnung XML files are accepted.");
        var cut = RenderAt("/");

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Xml, "photo.png"));

        cut.WaitForAssertion(() => Assert.Contains("Only XRechnung XML files are accepted.", cut.Find("[data-testid=upload-error]").TextContent, StringComparison.Ordinal));
        Assert.EndsWith("/", CurrentUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_file_links_to_the_invoice_it_already_created()
    {
        var existing = Guid.NewGuid();
        Intake.Result = new IntakeResult(IntakeOutcome.DuplicateFile, existing, Detail: "This file was uploaded before.");
        var cut = RenderAt("/");

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Xml, "invoice.xml"));

        cut.WaitForAssertion(() => Assert.Equal($"/invoices/{existing}", cut.Find("[data-testid=existing-invoice]").GetAttribute("href")));
    }

    [Fact]
    public void File_over_the_limit_is_refused_in_the_browser_without_calling_the_pipeline()
    {
        var cut = RenderAt("/");

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[MaxBytes + 1], "big.xml"));

        cut.WaitForAssertion(() => Assert.Contains("larger than", cut.Find("[data-testid=upload-error]").TextContent, StringComparison.Ordinal));
        Assert.Empty(Intake.Calls);
    }

    [Fact]
    public void Upload_input_accepts_xml_files()
    {
        var cut = RenderAt("/");

        Assert.Contains(".xml", cut.Find("input[type=file]").GetAttribute("accept"), StringComparison.Ordinal);
    }
}

/// <summary>
/// Before the page is interactive (server prerendering) nothing handles a picked file, so the upload is disabled
/// until then instead of silently doing nothing. Found by the browser test.
/// </summary>
public sealed class InboxPrerenderTests : UiTest
{
    [Fact]
    public void Upload_is_disabled_while_the_page_is_not_interactive()
    {
        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Static", isInteractive: false));
        NavigateTo("/");

        var cut = Render<Inbox>();

        Assert.True(cut.Find("input[type=file]").HasAttribute("disabled"));
    }

    [Fact]
    public void Upload_is_enabled_once_interactive()
    {
        NavigateTo("/");

        var cut = Render<Inbox>();

        Assert.False(cut.Find("input[type=file]").HasAttribute("disabled"));
    }
}
