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

    /// <summary>The user list (read-only in package 9).</summary>
    public const string ManageUsers = nameof(ManageUsers);

    internal static AuthorizationBuilder AddEInvoicePolicies(this AuthorizationBuilder builder) => builder
        .AddPolicy(ReadInvoices, p => p.RequireRole(Roles.Clerk, Roles.Approver, Roles.Auditor))
        .AddPolicy(UploadInvoices, p => p.RequireRole(Roles.Clerk))
        .AddPolicy(RematchInvoices, p => p.RequireRole(Roles.Clerk))
        .AddPolicy(ExplainWithAi, p => p.RequireRole(Roles.Clerk, Roles.Approver))
        .AddPolicy(ManageUsers, p => p.RequireRole(Roles.Admin));
}
