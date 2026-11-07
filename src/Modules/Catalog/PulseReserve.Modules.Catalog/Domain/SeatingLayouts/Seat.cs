using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

public sealed class Seat : Entity<SeatId>
{
    internal Seat(
        SeatId id,
        RowId rowId,
        SectionId sectionId,
        string label,
        SeatCoordinate coordinate,
        PriceTierId priceTierId) : base(id)
    {
        RowId = rowId;
        SectionId = sectionId;
        Label = label;
        Coordinate = coordinate;
        PriceTierId = priceTierId;
    }

    // EF Core materialization constructor
    private Seat() : base(SeatId.Empty)
    {
        RowId = RowId.Empty;
        SectionId = SectionId.Empty;
        Label = string.Empty;
        Coordinate = null!;
        PriceTierId = PriceTierId.Empty;
    }

    public RowId RowId { get; }
    public SectionId SectionId { get; }
    public string Label { get; }
    public SeatCoordinate Coordinate { get; }
    public PriceTierId PriceTierId { get; }

    public static Result<Seat> Create(
        SeatId id,
        RowId rowId,
        SectionId sectionId,
        string label,
        SeatCoordinate coordinate,
        PriceTierId priceTierId)
    {
        if (id == SeatId.Empty)
            return Result.Failure<Seat>(new Error("Seat.EmptyId", "Seat identifier cannot be empty."));

        if (rowId == RowId.Empty)
            return Result.Failure<Seat>(new Error("Seat.EmptyRowId", "Row identifier cannot be empty."));

        if (sectionId == SectionId.Empty)
            return Result.Failure<Seat>(new Error("Seat.EmptySectionId", "Section identifier cannot be empty."));

        if (string.IsNullOrWhiteSpace(label))
            return Result.Failure<Seat>(new Error("Seat.EmptyLabel", "Seat label cannot be empty."));

        if (coordinate is null)
            return Result.Failure<Seat>(new Error("Seat.NullCoordinate", "Seat coordinate is required."));

        if (priceTierId == PriceTierId.Empty)
            return Result.Failure<Seat>(new Error("Seat.EmptyPriceTierId", "Price tier identifier cannot be empty."));

        return Result.Success(new Seat(id, rowId, sectionId, label.Trim(), coordinate, priceTierId));
    }
}
