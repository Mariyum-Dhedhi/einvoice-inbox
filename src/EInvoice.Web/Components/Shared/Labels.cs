using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using MudBlazor;

namespace EInvoice.Web.Components.Shared;

/// <summary>Words, icons and colours for statuses and outcomes. The word and the icon carry the meaning; colour only supports it.</summary>
public static class Labels
{
    public static string Status(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Received => "Received",
        InvoiceStatus.Parsed => "Parsed",
        InvoiceStatus.Validated => "Validated",
        InvoiceStatus.Matched => "Matched",
        InvoiceStatus.Exception => "Exception",
        InvoiceStatus.Approved => "Approved",
        InvoiceStatus.Rejected => "Rejected",
        InvoiceStatus.Posted => "Posted",
        InvoiceStatus.Failed => "Failed",
        _ => status.ToString(),
    };

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

    public static string Outcome(MatchOutcome outcome) => outcome switch
    {
        MatchOutcome.FullMatch => "Matches order",
        MatchOutcome.PriceVariance => "Price above order",
        MatchOutcome.QuantityVariance => "More than received",
        MatchOutcome.NoPurchaseOrder => "No order line",
        MatchOutcome.NoGoodsReceipt => "Nothing received",
        _ => outcome.ToString(),
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
}
