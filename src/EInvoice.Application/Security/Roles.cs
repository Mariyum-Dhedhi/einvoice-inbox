namespace EInvoice.Application.Security;

/// <summary>
/// The four roles, with separated duties: whoever uploads cannot approve, the auditor only reads, and the admin
/// manages users and master data but never touches invoices. What each role may do is decided per endpoint.
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

    public static IReadOnlyList<string> All { get; } = [Clerk, Approver, Auditor, Admin];
}
