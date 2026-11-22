using EInvoice.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace EInvoice.Web.Security;

/// <summary>
/// What may be done, by whom. Endpoints and pages name a policy, never a list of roles, so the rules live here once.
/// Separated duties: the uploader cannot approve, the auditor only reads, the admin never touches invoices.
/// </summary>
public static class Policies
{
    /// <summary>Inbox, invoice detail, the original file, the rule-based explanation.</summary>
    public const string ReadInvoices = nameof(ReadInvoices);

    public const string UploadInvoices = nameof(UploadInvoices);

    public const string RematchInvoices = nameof(RematchInvoices);

    /// <summary>Asking the language model for an explanation (a suggestion for whoever decides).</summary>
    public const string ExplainWithAi = nameof(ExplainWithAi);

    /// <summary>Approve or reject an invoice. Not the clerk who uploads: separated duties.</summary>
    public const string DecideInvoices = nameof(DecideInvoices);

    /// <summary>The audit trail and its verification.</summary>
    public const string ReadAudit = nameof(ReadAudit);

    /// <summary>Vendor master data and bank accounts (two-person rule for new accounts).</summary>
    public const string ManageVendors = nameof(ManageVendors);

    /// <summary>The user list (read-only in package 9).</summary>
    public const string ManageUsers = nameof(ManageUsers);

    /// <summary>Purchase orders and goods receipts: what invoices are matched against. Not the admin (no invoice work).</summary>
    public const string ReadPurchasing = nameof(ReadPurchasing);

    /// <summary>Adding orders and goods receipts: the buyer only, who touches no invoices (separated duties).</summary>
    public const string ManagePurchasing = nameof(ManagePurchasing);

    /// <summary>The ERP sync's API: vendors, orders and receipts from the ERP. Only its API key carries this role.</summary>
    public const string SyncMasterData = nameof(SyncMasterData);

    internal static AuthorizationBuilder AddEInvoicePolicies(this AuthorizationBuilder builder) => builder
        .AddPolicy(ReadInvoices, p => p.RequireRole(Roles.Clerk, Roles.Approver, Roles.Auditor))
        .AddPolicy(UploadInvoices, p => p.RequireRole(Roles.Clerk))
        .AddPolicy(RematchInvoices, p => p.RequireRole(Roles.Clerk))
        .AddPolicy(ExplainWithAi, p => p.RequireRole(Roles.Clerk, Roles.Approver))
        .AddPolicy(DecideInvoices, p => p.RequireRole(Roles.Approver))
        .AddPolicy(ReadAudit, p => p.RequireRole(Roles.Auditor))
        .AddPolicy(ManageUsers, p => p.RequireRole(Roles.Admin))
        .AddPolicy(ManageVendors, p => p.RequireRole(Roles.Admin))
        .AddPolicy(ReadPurchasing, p => p.RequireRole(Roles.Clerk, Roles.Approver, Roles.Auditor, Roles.Buyer))
        .AddPolicy(ManagePurchasing, p => p.RequireRole(Roles.Buyer))
        .AddPolicy(SyncMasterData, p => p.RequireRole(Roles.Integration));
}
