using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.ValueObjects;

public sealed class SeatCoordinate : ValueObject
{
    private SeatCoordinate(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; }
    public int Y { get; }

    public static Result<SeatCoordinate> Create(int x, int y)
    {
        if (x < 0 || y < 0)
        {
            return Result.Failure<SeatCoordinate>(new Error("SeatCoordinate.Negative", "Seat coordinates cannot be negative."));
        }

        return Result.Success(new SeatCoordinate(x, y));
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return X;
        yield return Y;
    }
}
