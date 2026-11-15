using System.Globalization;
using System.Security.Claims;
using EInvoice.Application.Approval;

namespace EInvoice.Web.Security;

/// <summary>
/// The approver behind a sign-in: the user name, and the limit stored on the account (claim) or else the configured
/// default. A limit that cannot be read without doubt (not a plain non-negative number, or more than one) is 0:
/// no authority, never "unlimited".
/// </summary>
public static class ApproverOf
{
    public static Approver User(ClaimsPrincipal user, decimal defaultLimitEur)
    {
        ArgumentNullException.ThrowIfNull(user);
        var name = SignedInUser.Name(user);
        var limits = user.FindAll(ApprovalLimits.ClaimType).Select(c => c.Value).ToList();
        return limits.Count switch
        {
            0 => new Approver(name, defaultLimitEur),
            1 => new Approver(name, Parse(limits[0])),
            _ => new Approver(name, 0m),
        };
    }

    // Invariant notation with a decimal point only: no sign, no thousands separator, no exponent.
    private static decimal Parse(string value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var limit) ? limit : 0m;
}
