using EInvoice.Application.Extraction;

namespace EInvoice.UnitTests.Extraction;

/// <summary>
/// Before any text goes to a model, bank accounts and contact details are replaced by placeholders, so a hosted
/// provider could be used later without sending them out. IBAN placeholders can be turned back afterwards.
/// </summary>
public class SensitiveDataMaskerTests
{
    private const string Iban1 = "DE72000000000000001001";

    [Theory]
    [InlineData("IBAN: DE72 0000 0000 0000 0010 01")]
    [InlineData("IBAN: DE72000000000000001001")]
    [InlineData("Bank: Musterbank, IBAN DE72 0000 0000 0000 0010 01, BIC MUSTDEFFXXX")]
    public void Iban_is_replaced_by_a_placeholder_and_remembered_without_spaces(string text)
    {
        var masked = SensitiveDataMasker.Mask(text);

        Assert.DoesNotContain("DE72", masked.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("0010 01", masked.Text, StringComparison.Ordinal);
        Assert.Contains("[IBAN_1]", masked.Text, StringComparison.Ordinal);
        Assert.Equal(Iban1, masked.Ibans["[IBAN_1]"]);
    }

    [Fact]
    public void Same_iban_twice_gets_the_same_placeholder_and_a_second_iban_its_own()
    {
        var masked = SensitiveDataMasker.Mask("IBAN: DE72 0000 0000 0000 0010 01\nor FR76 3000 6000 0112 3456 7890 189\nagain DE72000000000000001001");

        Assert.Equal(2, masked.Ibans.Count);
        Assert.Equal(2, masked.Text.Split("[IBAN_1]").Length - 1);
        Assert.Contains("[IBAN_2]", masked.Text, StringComparison.Ordinal);
        Assert.Equal("FR7630006000011234567890189", masked.Ibans["[IBAN_2]"]);
    }

    [Theory]
    [InlineData("Ansprechpartner: Erika Mustermann")]
    [InlineData("Ihr Ansprechpartner: Herr Max Mustermann")]
    [InlineData("Contact: Erika Mustermann")]
    [InlineData("Kontakt: Erika Mustermann")]
    [InlineData("Sachbearbeiter: Erika Mustermann")]
    [InlineData("Sachbearbeiterin: Erika Mustermann")]
    public void Contact_name_after_a_label_is_masked(string line)
    {
        var masked = SensitiveDataMasker.Mask($"Beispiel Buerobedarf GmbH\n{line}\nInvoice number: RE-1");

        Assert.DoesNotContain("Mustermann", masked.Text, StringComparison.Ordinal);
        Assert.Contains("[CONTACT]", masked.Text, StringComparison.Ordinal);
        Assert.Contains("Beispiel Buerobedarf GmbH", masked.Text, StringComparison.Ordinal); // the company is needed
        Assert.Contains("Invoice number: RE-1", masked.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Email_addresses_and_phone_numbers_are_masked()
    {
        var masked = SensitiveDataMasker.Mask("E-Mail: erika.mustermann@beispiel.example\nTel.: +49 30 1234567\nMobil: 0170 1234567\nFax +49 (30) 123 45 68");

        Assert.DoesNotContain("@", masked.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567", masked.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("123 45 68", masked.Text, StringComparison.Ordinal);
        Assert.Contains("[EMAIL]", masked.Text, StringComparison.Ordinal);
        Assert.Contains("[PHONE]", masked.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Invoice_content_the_model_needs_is_left_alone()
    {
        const string text = "USt-IdNr.: DE000000001\nInvoice number: RE-2026-1001\nInvoice date: 02.11.2026\nPurchase order: PO-0042\n1 | HDMI cable 2 m | 10 | 7,41 | 74,10 | 19 % | 1\nGross total: 1.234,56 EUR";

        var masked = SensitiveDataMasker.Mask(text);

        Assert.Equal(text, masked.Text);
        Assert.Empty(masked.Ibans);
    }

    [Fact]
    public void A_placeholder_already_in_the_document_cannot_pose_as_a_masked_iban()
    {
        // The supplier writes "[IBAN_1]" into the document, hoping it is swapped for a real account later.
        var masked = SensitiveDataMasker.Mask("Pay to [IBAN_1] please. IBAN: DE72 0000 0000 0000 0010 01");

        Assert.Equal(1, masked.Text.Split("[IBAN_1]").Length - 1); // only our own placeholder is left
        Assert.Equal(Iban1, masked.Ibans["[IBAN_1]"]);
    }

    [Fact]
    public void Unmask_restores_known_placeholders_only()
    {
        var masked = SensitiveDataMasker.Mask("IBAN: DE72 0000 0000 0000 0010 01");

        Assert.Equal(Iban1, masked.Unmask("[IBAN_1]"));
        Assert.Null(masked.Unmask("[IBAN_7]"));
        Assert.Null(masked.Unmask("DE89370400440532013000")); // an account that was never in the document
    }
}
