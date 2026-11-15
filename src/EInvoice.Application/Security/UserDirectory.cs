namespace EInvoice.Application.Security;

/// <summary>An account as the admin's list shows it: no password hash, no security stamp, nothing else.</summary>
public sealed record UserSummary(string UserName, IReadOnlyList<string> Roles, DateTimeOffset? LockedUntil);

/// <summary>Read-only list of the accounts (package 9); changing them arrives with package 11.</summary>
public interface IUserDirectory
{
    /// <summary>Every account, by user name; each account's roles by name; a lockout only while it lasts.</summary>
    Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken);
}
