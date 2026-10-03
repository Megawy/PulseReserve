namespace PulseReserve.Modules.Catalog.Contracts;

public readonly record struct EventId(Guid Value)
{
    public static EventId New() => new(Guid.NewGuid());
    public static EventId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

public readonly record struct VenueId(Guid Value)
{
    public static VenueId New() => new(Guid.NewGuid());
    public static VenueId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

public readonly record struct SeatingLayoutId(Guid Value)
{
    public static SeatingLayoutId New() => new(Guid.NewGuid());
    public static SeatingLayoutId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

public readonly record struct SectionId(Guid Value)
{
    public static SectionId New() => new(Guid.NewGuid());
    public static SectionId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

public readonly record struct RowId(Guid Value)
{
    public static RowId New() => new(Guid.NewGuid());
    public static RowId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

public readonly record struct SeatId(Guid Value)
{
    public static SeatId New() => new(Guid.NewGuid());
    public static SeatId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}

public readonly record struct PriceTierId(Guid Value)
{
    public static PriceTierId New() => new(Guid.NewGuid());
    public static PriceTierId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
