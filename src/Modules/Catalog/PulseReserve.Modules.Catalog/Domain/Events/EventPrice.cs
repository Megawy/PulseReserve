using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.Events;

public sealed class EventPrice : ValueObject
{
    private EventPrice(PriceTierId priceTierId, Money price)
    {
        PriceTierId = priceTierId;
        Price = price;
    }

    // EF Core materialization constructor
    private EventPrice()
    {
        PriceTierId = PriceTierId.Empty;
        Price = null!;
    }

    public PriceTierId PriceTierId { get; }
    public Money Price { get; }

    public static Result<EventPrice> Create(PriceTierId priceTierId, Money price)
    {
        if (priceTierId == PriceTierId.Empty)
            return Result.Failure<EventPrice>(new Error("EventPrice.EmptyPriceTierId", "Price tier identifier cannot be empty."));

        if (price is null)
            return Result.Failure<EventPrice>(new Error("EventPrice.NullPrice", "Price is required."));

        return Result.Success(new EventPrice(priceTierId, price));
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return PriceTierId;
        yield return Price;
    }
}
