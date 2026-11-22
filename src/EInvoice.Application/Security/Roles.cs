namespace EInvoice.Application.Security;

/// <summary>
/// The five roles, with separated duties: whoever uploads cannot approve, the auditor only reads, the admin
/// manages users and master data but never touches invoices, and the buyer records what was ordered and received
/// (what invoices are matched against) but never touches invoices either. What each role may do is decided per endpoint.
/// </summary>
public static class Roles
{
    /// <summary>Accounts-payable clerk: uploads invoices, re-runs the match, reads everything about invoices.</summary>
    public const string Clerk = "Clerk";

    /// <summary>Approves or rejects exceptions (package 10); reads invoices.</summary>
    public const string Approver = "Approver";

    /// <summary>Reads invoices and the audit trail (package 11); changes nothing.</summary>
    public const string Auditor = "Auditor";

    /// <summary>Manages users and vendor master data, including bank accounts (package 11); no access to invoices.</summary>
    public const string Admin = "Admin";

    /// <summary>Records purchase orders and goods receipts; reads no invoices.</summary>
    public const string Buyer = "Buyer";

    /// <summary>
    /// The ERP sync (package 16c), signed in with its API key for one request. Not an account role: no user can hold
    /// it, so no person signs in with it. Not in <see cref="All"/>.
    /// </summary>
    public const string Integration = "Integration";

    /// <summary>The roles people get.</summary>
    public static IReadOnlyList<string> All { get; } = [Clerk, Approver, Auditor, Admin, Buyer];
}
