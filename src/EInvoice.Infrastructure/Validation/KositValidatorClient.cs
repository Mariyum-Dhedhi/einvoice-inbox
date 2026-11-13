using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EInvoice.Application.Validation;
using EInvoice.Infrastructure.Parsing;

namespace EInvoice.Infrastructure.Validation;

/// <summary>
/// Client for the KoSIT validator in daemon mode (docker/kosit-validator). The HTTP status is the verdict:
/// 200 acceptable, 406 not acceptable. Everything else, including timeouts and connection errors, is
/// <see cref="ConformanceOutcome.Unavailable"/>: the check fails closed and never reports "accepted" by accident.
/// </summary>
/// <param name="http">Configured with the validator base address and a timeout.</param>
public sealed partial class KositValidatorClient(HttpClient http) : IConformanceValidator
{
    private static readonly XNamespace Varl = "http://www.xoev.de/de/validator/varl/1";

    public async Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken)
    {
        using var content = new ReadOnlyMemoryContent(xml);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri(string.Empty, UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ConformanceResult.Unavailable;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed. A cancellation requested by the caller is passed on instead.
            return ConformanceResult.Unavailable;
        }

        using (response)
        {
            return response.StatusCode switch
            {
                HttpStatusCode.OK => ConformanceResult.Accepted,
                HttpStatusCode.NotAcceptable => ConformanceResult.Rejected(await ReadErrorCodesAsync(response, cancellationToken).ConfigureAwait(false)),
                _ => ConformanceResult.Unavailable,
            };
        }
    }

    /// <summary>
    /// Rule codes of error-level messages in the VARL report. The 406 status alone decides the rejection;
    /// a report that cannot be read only means no codes can be shown. The report goes through SafeXml like any XML.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadErrorCodesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var report = SafeXml.Load(body, XmlLimits.Default);
            return report.Descendants(Varl + "message")
                .Where(m => (string?)m.Attribute("level") == "error")
                .Select(m => (string?)m.Attribute("code"))
                .OfType<string>()
                .Where(code => RuleCode().IsMatch(code))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (DocumentRejectedException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Rule codes look like BR-CO-15, BR-DE-1 or PEPPOL-EN16931-R001; anything else is not shown.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$")]
    private static partial Regex RuleCode();
}
