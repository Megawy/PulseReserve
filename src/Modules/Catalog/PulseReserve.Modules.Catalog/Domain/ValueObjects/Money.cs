using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.ValueObjects;

public sealed class Money : ValueObject
{
    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }
    public string Currency { get; }

    public static Result<Money> Create(decimal amount, string currency)
    {
        if (amount < 0)
        {
            return Result.Failure<Money>(new Error("Money.NegativeAmount", "Price cannot be negative."));
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            return Result.Failure<Money>(new Error("Money.InvalidCurrency", "Currency must be a valid 3-letter ISO code."));
        }

        return Result.Success(new Money(amount, currency.Trim().ToUpperInvariant()));
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }
}
