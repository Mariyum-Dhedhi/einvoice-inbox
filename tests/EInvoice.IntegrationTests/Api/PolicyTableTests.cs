using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// Every policy of the application against every role, with the application's own authorization service.
/// RoleMatrixTests covers policies through endpoints; this also covers the ones used only by buttons (ExplainWithAi)
/// and pins the complete list, so a new policy needs a decision here. Written by hand, like the access table.
/// </summary>
public sealed class PolicyTableTests
{
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";

    private static readonly Dictionary<string, string[]> Expected = new(StringComparer.Ordinal)
    {
        ["ReadInvoices"] = ["Clerk", "Approver", "Auditor"],
        ["UploadInvoices"] = ["Clerk"],
        ["RematchInvoices"] = ["Clerk"],
        ["ExplainWithAi"] = ["Clerk", "Approver"],
        ["DecideInvoices"] = ["Approver"], // approve and reject; a clerk who uploads never decides
        ["ReadAudit"] = ["Auditor"], // the audit trail and its verification
        ["ManageUsers"] = ["Admin"],
        ["ManageVendors"] = ["Admin"], // vendor master data and bank accounts
        ["ReadPurchasing"] = ["Clerk", "Approver", "Auditor", "Buyer"], // orders and receipts: what invoices are matched against
        ["ManagePurchasing"] = ["Buyer"], // adds orders and receipts; never touches invoices
    };

    private static readonly string[] AllRoles = ["Clerk", "Approver", "Auditor", "Admin", "Buyer"];

    public static TheoryData<string, string> Cases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var policy in Expected.Keys)
        {
            foreach (var role in AllRoles.Append(AccessTable.NoRole))
            {
                cases.Add(policy, role);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Policy_admits_exactly_its_roles(string policy, string role)
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        var claims = new List<Claim> { new(ClaimTypes.Name, "someone@test") };
        if (role != AccessTable.NoRole)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var result = await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), policy);

        Assert.Equal(Expected[policy].Contains(role), result.Succeeded);
    }

    [Fact]
    public void The_policy_list_is_complete()
    {
        var declared = typeof(EInvoice.Web.Security.Policies).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(Expected.Keys.Order(StringComparer.Ordinal), declared);
    }
}
