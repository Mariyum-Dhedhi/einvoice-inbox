using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using EInvoice.Application.Approval;
using EInvoice.Application.Audit;
using EInvoice.Application.Intake;
using EInvoice.Application.Matching;
using EInvoice.Application.Security;
using EInvoice.Application.Validation;
using EInvoice.Application.Vendors;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;

namespace EInvoice.ComponentTests.Localization;

/// <summary>
/// The UI texts as the app loads them: the compiled resources (English neutral, German satellite), not the .resx files,
/// so a German file that is not built into the app counts as missing.
/// </summary>
internal static class TextResources
{
    private static readonly ResourceManager Manager = new("EInvoice.Web.Resources.Texts", typeof(EInvoice.Web.Texts).Assembly);

    public static IReadOnlyDictionary<string, string> English { get; } = Load(CultureInfo.InvariantCulture);

    public static IReadOnlyDictionary<string, string> German { get; } = Load(CultureInfo.GetCultureInfo("de"));

    /// <summary>
    /// Texts named by code at run time (Status.Matched, Rule.IBAN_NOT_APPROVED.Title, ...): one for every value the
    /// code can have. A new status, outcome, rule code, audit action or role fails the tests until it has its words.
    /// </summary>
    public static IReadOnlySet<string> ByCode { get; } = BuildByCode();

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string WebProject => Path.Combine(RepositoryRoot, "src", "EInvoice.Web");

    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No texts for '{culture.Name}' are built into EInvoice.Web.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => e.Value as string ?? string.Empty, StringComparer.Ordinal);
    }

    private static HashSet<string> BuildByCode()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        keys.UnionWith(Enum.GetNames<InvoiceStatus>().Select(n => $"Status.{n}"));
        keys.UnionWith(Enum.GetNames<MatchOutcome>().Select(n => $"Outcome.{n}"));
        keys.UnionWith(Enum.GetNames<FindingSeverity>().Select(n => $"Severity.{n}"));
        foreach (var outcome in Enum.GetNames<MatchOutcome>().Where(n => n != nameof(MatchOutcome.FullMatch)))
        {
            keys.Add($"Explain.{outcome}.Text");
            keys.Add($"Explain.{outcome}.Step");
        }

        foreach (var code in Constants(typeof(RuleCodes)))
        {
            keys.Add($"Rule.{code}.Title");
            keys.Add($"Rule.{code}.Step");
        }

        keys.UnionWith(Enum.GetNames<IntakeOutcome>().Where(n => n != nameof(IntakeOutcome.Created)).Select(n => $"Intake.{n}"));
        keys.UnionWith(Enum.GetNames<DecisionOutcome>().Select(n => $"Decision.{n}"));
        keys.UnionWith(Enum.GetNames<RematchOutcome>().Select(n => $"Rematch.{n}"));
        keys.UnionWith(Enum.GetNames<BankAccountOutcome>().Select(n => $"BankAccount.{n}"));
        keys.UnionWith(Enum.GetNames<AuditProblem>().Select(n => $"AuditProblem.{n}"));
        keys.UnionWith(Constants(typeof(AuditActions)).Select(a => $"AuditAction.{a}"));
        keys.UnionWith(Constants(typeof(Roles)).Select(r => $"Role.{r}"));
        return keys;
    }

    internal static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EInvoice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (EInvoice.slnx) not found.");
    }
}
