using EInvoice.Application.Parsing;
using EInvoice.Infrastructure.Parsing;

namespace EInvoice.UnitTests.Parsing;

/// <summary>
/// Which ZUGFeRD / Factur-X profiles are accepted, recognised by their official identifier (BT-24): BASIC and above
/// carry invoice lines and can be checked; MINIMUM and BASIC WL cannot. Only the XRECHNUNG profile goes to KoSIT,
/// whose configuration knows XRechnung only.
/// </summary>
public sealed class ZugferdProfilesTests
{
    [Theory]
    [InlineData("urn:factur-x.eu:1p0:minimum", ZugferdProfile.Minimum)]
    [InlineData("urn:factur-x.eu:1p0:basicwl", ZugferdProfile.BasicWl)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic", ZugferdProfile.Basic)]
    [InlineData("urn:cen.eu:en16931:2017", ZugferdProfile.En16931)]
    [InlineData("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended", ZugferdProfile.Extended)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", ZugferdProfile.XRechnung)]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_2.3", ZugferdProfile.XRechnung)]
    [InlineData("  urn:cen.eu:en16931:2017  ", ZugferdProfile.En16931)] // surrounding spaces from pretty-printed XML
    [InlineData("urn:something:else", ZugferdProfile.Unknown)]
    [InlineData("", ZugferdProfile.Unknown)]
    [InlineData(null, ZugferdProfile.Unknown)]
    public void Profiles_are_recognised_by_their_official_identifier(string? identifier, ZugferdProfile expected)
    {
        Assert.Equal(expected, ZugferdProfiles.Classify(identifier));
    }

    [Theory]
    [InlineData(ZugferdProfile.Basic, true)]
    [InlineData(ZugferdProfile.En16931, true)]
    [InlineData(ZugferdProfile.Extended, true)]
    [InlineData(ZugferdProfile.XRechnung, true)]
    [InlineData(ZugferdProfile.Minimum, false)] // no invoice lines: nothing to match or check
    [InlineData(ZugferdProfile.BasicWl, false)]
    [InlineData(ZugferdProfile.Unknown, false)]
    public void Basic_and_above_are_accepted(ZugferdProfile profile, bool accepted)
    {
        Assert.Equal(accepted, ZugferdProfiles.IsAccepted(profile));
    }

    [Theory]
    [InlineData(ZugferdProfile.XRechnung, true)]
    [InlineData(ZugferdProfile.Basic, false)]
    [InlineData(ZugferdProfile.En16931, false)]
    [InlineData(ZugferdProfile.Extended, false)]
    public void Only_the_xrechnung_profile_goes_to_kosit(ZugferdProfile profile, bool checkedByKosit)
    {
        Assert.Equal(checkedByKosit, ZugferdProfiles.CheckedByKosit(profile));
    }

    [Fact]
    public void Refusals_name_the_profile_and_say_what_to_send()
    {
        var reason = ZugferdProfiles.RefusalReason(ZugferdProfile.Minimum);

        Assert.Contains("MINIMUM", reason, StringComparison.Ordinal);
        Assert.Contains("BASIC", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01.01a-INVOICE_uncefact.xml")]
    [InlineData("01.01a-INVOICE_ubl.xml")]
    public void The_parser_reads_the_profile_identifier_bt_24(string sample)
    {
        using var file = File.OpenRead(Path.Combine(TestPaths.KositCorpus, sample));

        var parsed = new XRechnungParser().Parse(file);

        Assert.True(parsed.Succeeded, parsed.Error?.Message);
        Assert.Equal("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", parsed.Document!.SpecificationIdentifier);
    }
}
