namespace EInvoice.Web.Api;

/// <summary>Who is making the request; recorded as the submitter and as the actor in the status history.</summary>
public interface ICurrentUser
{
    string Name { get; }
}

/// <summary>Placeholder until sign-in exists (package 9). Then this is replaced by the authenticated user.</summary>
internal sealed class AnonymousUser : ICurrentUser
{
    public string Name => "anonymous";
}
