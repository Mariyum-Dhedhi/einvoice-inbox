using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace EInvoice.Web.Components.Shared;

/// <summary>
/// Words, icons and colours for statuses, outcomes and codes. The word and the icon carry the meaning; colour only
/// supports it. The words come from the texts by code (Status.Matched, Rule.IBAN_NOT_APPROVED.Title, ...), so every
/// status and code has them in both languages (TextResourcesTests).
/// </summary>
public static class Labels
{
    public static string Status(IStringLocalizer t, InvoiceStatus status) => t[$"Status.{status}"];

    public static string Outcome(IStringLocalizer t, MatchOutcome outcome) => t[$"Outcome.{outcome}"];

    public static string Severity(IStringLocalizer t, FindingSeverity severity) => t[$"Severity.{severity}"];

    /// <summary>A rule's name; a code this version does not know (an older stored finding) is shown as it is.</summary>
    public static string Rule(IStringLocalizer t, string code) => OrCode(t[$"Rule.{code}.Title"], code);

    public static string Role(IStringLocalizer t, string role) => OrCode(t[$"Role.{role}"], role);

    public static string AuditAction(IStringLocalizer t, string action) => OrCode(t[$"AuditAction.{action}"], action);

    public static string StatusIcon(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Matched => Icons.Material.Outlined.CheckCircle,
        InvoiceStatus.Exception => Icons.Material.Outlined.ReportProblem,
        InvoiceStatus.Failed => Icons.Material.Outlined.ErrorOutline,
        InvoiceStatus.Approved => Icons.Material.Outlined.ThumbUp,
        InvoiceStatus.Rejected => Icons.Material.Outlined.Block,
        InvoiceStatus.Posted => Icons.Material.Outlined.Inventory2,
        _ => Icons.Material.Outlined.HourglassEmpty,
    };

    public static Color StatusColor(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Matched or InvoiceStatus.Approved or InvoiceStatus.Posted => Color.Success,
        InvoiceStatus.Exception => Color.Warning,
        InvoiceStatus.Failed or InvoiceStatus.Rejected => Color.Error,
        _ => Color.Default,
    };

    public static string OutcomeIcon(MatchOutcome outcome) => outcome switch
    {
        MatchOutcome.FullMatch => Icons.Material.Outlined.CheckCircle,
        MatchOutcome.PriceVariance => Icons.Material.Outlined.Euro,
        MatchOutcome.QuantityVariance => Icons.Material.Outlined.Inventory,
        MatchOutcome.NoPurchaseOrder => Icons.Material.Outlined.LinkOff,
        _ => Icons.Material.Outlined.LocalShipping,
    };

    public static Color OutcomeColor(MatchOutcome outcome) => outcome == MatchOutcome.FullMatch ? Color.Success : Color.Warning;

    public static Color SeverityColor(FindingSeverity severity) => severity == FindingSeverity.Blocking ? Color.Error : Color.Warning;

    private static string OrCode(LocalizedString text, string code) => text.ResourceNotFound ? code : text.Value;
}
