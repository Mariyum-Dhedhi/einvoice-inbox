namespace EInvoice.Domain.Shared;

/// <summary>Base type for errors that mean a business rule was broken, as opposed to a technical failure.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }
}

public sealed class CurrencyMismatchException : DomainException
{
    public CurrencyMismatchException(string left, string right)
        : base($"Cannot combine amounts in {left} and {right}.")
    {
        Left = left;
        Right = right;
    }

    public string Left { get; }

    public string Right { get; }
}
