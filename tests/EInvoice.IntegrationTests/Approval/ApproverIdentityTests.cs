using System.Security.Claims;
using EInvoice.Application.Approval;
using EInvoice.IntegrationTests.Api;
using EInvoice.Web.Configuration;
using EInvoice.Web.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EInvoice.IntegrationTests.Approval;

/// <summary>
/// Who decides and up to which amount, read from the sign-in. A limit that cannot be read means no authority:
/// it fails closed (0), never open.
/// </summary>
public sealed class ApproverIdentityTests
{
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";
    private const decimal DefaultLimit = 10_000m;

    private static ClaimsPrincipal User(params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "approver@test"), .. extra], "Test"));

    [Fact]
    public void Without_a_limit_on_the_account_the_default_applies()
    {
        Assert.Equal(new Approver("approver@test", DefaultLimit), ApproverOf.User(User(), DefaultLimit));
    }

    [Theory]
    [InlineData("250", 250)]
    [InlineData("250.50", 250.50)]
    [InlineData("0", 0)]
    public void A_limit_on_the_account_replaces_the_default(string claim, decimal expected)
    {
        Assert.Equal(expected, ApproverOf.User(User(new Claim(ApprovalLimits.ClaimType, claim)), DefaultLimit).LimitEur);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1,000")]   // a German or English thousands separator: ambiguous, so refused
    [InlineData("1e6")]
    [InlineData("")]
    public void An_unreadable_limit_means_no_authority(string claim)
    {
        Assert.Equal(0m, ApproverOf.User(User(new Claim(ApprovalLimits.ClaimType, claim)), DefaultLimit).LimitEur);
    }

    [Fact]
    public void Two_limits_on_one_account_means_no_authority()
    {
        var user = User(new Claim(ApprovalLimits.ClaimType, "100"), new Claim(ApprovalLimits.ClaimType, "100000"));

        Assert.Equal(0m, ApproverOf.User(user, DefaultLimit).LimitEur); // which one was meant? Refuse rather than guess.
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1000000001")]
    [InlineData("not a number")]
    public async Task A_wrong_default_limit_stops_the_application_at_startup(string value)
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["Approval:DefaultLimitEur"] = value;
        await using var factory = new ApiFactory(settings);

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(error);
        Assert.Contains("DefaultLimitEur", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_limit_comes_from_settings()
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["Approval:DefaultLimitEur"] = "2500.00";
        await using var factory = new ApiFactory(settings);

        Assert.Equal(2_500m, factory.Services.GetRequiredService<IOptions<ApprovalSettings>>().Value.DefaultLimitEur);
    }
}
