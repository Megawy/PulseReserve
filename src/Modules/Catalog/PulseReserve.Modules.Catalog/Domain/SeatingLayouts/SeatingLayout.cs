using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

// SeatingLayout is the Aggregate Root for physical seating structure (Sections, Rows, Seats, and Tier categories).
// Concurrency Boundary: SeatingLayoutId + ExpectedVersion. Child entity IDs are not independent concurrency boundaries.
// Structural Reusability: A single locked layout can be referenced concurrently across multiple independent Events.
public sealed class SeatingLayout : AggregateRoot<SeatingLayoutId>
{
    private readonly List<PriceTier> _priceTiers = [];
    private readonly List<Section> _sections = [];
    private readonly List<Row> _rows = [];
    private readonly List<Seat> _seats = [];

    private SeatingLayout(SeatingLayoutId id, VenueId venueId, string name) : base(id)
    {
        VenueId = venueId;
        Name = name;
        IsLocked = false;
    }

    public VenueId VenueId { get; }
    public string Name { get; private set; }

    // Once locked (triggered when a linked Event enters OnSale), the physical structure becomes permanently immutable.
    public bool IsLocked { get; private set; }

    public IReadOnlyCollection<PriceTier> PriceTiers => _priceTiers.AsReadOnly();
    public IReadOnlyCollection<Section> Sections => _sections.AsReadOnly();
    public IReadOnlyCollection<Row> Rows => _rows.AsReadOnly();
    public IReadOnlyCollection<Seat> Seats => _seats.AsReadOnly();

    public static Result<SeatingLayout> Create(SeatingLayoutId id, VenueId venueId, string name)
    {
        if (id == SeatingLayoutId.Empty)
            return Result.Failure<SeatingLayout>(new Error("SeatingLayout.EmptyId", "Seating layout identifier cannot be empty."));

        if (venueId == VenueId.Empty)
            return Result.Failure<SeatingLayout>(new Error("SeatingLayout.EmptyVenueId", "Venue identifier cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<SeatingLayout>(new Error("SeatingLayout.EmptyName", "Seating layout name cannot be empty."));

        return Result.Success(new SeatingLayout(id, venueId, name.Trim()));
    }

    public Result AddPriceTier(PriceTier priceTier)
    {
        if (IsLocked)
            return Result.Failure(Error.Conflict("SeatingLayout.Locked", "Cannot modify a locked seating layout."));

        if (priceTier is null)
            return Result.Failure(new Error("SeatingLayout.NullPriceTier", "Price tier cannot be null."));

        if (_priceTiers.Any(pt => pt.Id == priceTier.Id))
            return Result.Failure(new Error("SeatingLayout.DuplicatePriceTierId", $"Price tier with ID '{priceTier.Id}' already exists."));

        _priceTiers.Add(priceTier);
        return Result.Success();
    }

    public Result AddSection(Section section)
    {
        if (IsLocked)
            return Result.Failure(Error.Conflict("SeatingLayout.Locked", "Cannot modify a locked seating layout."));

        if (section is null)
            return Result.Failure(new Error("SeatingLayout.NullSection", "Section cannot be null."));

        if (_sections.Any(s => s.Id == section.Id))
            return Result.Failure(new Error("SeatingLayout.DuplicateSectionId", $"Section with ID '{section.Id}' already exists."));

        if (_sections.Any(s => s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure(new Error("SeatingLayout.DuplicateSectionName", $"Section with name '{section.Name}' already exists in this layout."));

        _sections.Add(section);
        return Result.Success();
    }

    public Result AddRow(Row row)
    {
        if (IsLocked)
            return Result.Failure(Error.Conflict("SeatingLayout.Locked", "Cannot modify a locked seating layout."));

        if (row is null)
            return Result.Failure(new Error("SeatingLayout.NullRow", "Row cannot be null."));

        if (_rows.Any(r => r.Id == row.Id))
            return Result.Failure(new Error("SeatingLayout.DuplicateRowId", $"Row with ID '{row.Id}' already exists."));

        var parentSection = _sections.FirstOrDefault(s => s.Id == row.SectionId);
        if (parentSection is null)
            return Result.Failure(new Error("SeatingLayout.ParentSectionNotFound", $"Parent section with ID '{row.SectionId}' does not exist in this layout."));

        if (_rows.Any(r => r.SectionId == row.SectionId && r.Label.Equals(row.Label, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure(new Error("SeatingLayout.DuplicateRowLabel", $"Row with label '{row.Label}' already exists in section '{parentSection.Name}'."));

        _rows.Add(row);
        return Result.Success();
    }

    public Result AddSeat(Seat seat)
    {
        if (IsLocked)
            return Result.Failure(Error.Conflict("SeatingLayout.Locked", "Cannot modify a locked seating layout."));

        if (seat is null)
            return Result.Failure(new Error("SeatingLayout.NullSeat", "Seat cannot be null."));

        if (_seats.Any(s => s.Id == seat.Id))
            return Result.Failure(new Error("SeatingLayout.DuplicateSeatId", $"Seat with ID '{seat.Id}' already exists."));

        var parentRow = _rows.FirstOrDefault(r => r.Id == seat.RowId && r.SectionId == seat.SectionId);
        if (parentRow is null)
            return Result.Failure(new Error("SeatingLayout.ParentRowNotFound", $"Parent row with ID '{seat.RowId}' in section '{seat.SectionId}' does not exist."));

        if (!_priceTiers.Any(pt => pt.Id == seat.PriceTierId))
            return Result.Failure(new Error("SeatingLayout.PriceTierNotFound", $"Price tier with ID '{seat.PriceTierId}' does not exist in this layout."));

        if (_seats.Any(s => s.RowId == seat.RowId && s.Label.Equals(seat.Label, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure(new Error("SeatingLayout.DuplicateSeatLabel", $"Seat with label '{seat.Label}' already exists in row '{parentRow.Label}'."));

        _seats.Add(seat);
        return Result.Success();
    }

    public Result LockLayout()
    {
        var validationResult = ValidateStructure();
        if (validationResult.IsFailure)
            return validationResult;

        IsLocked = true;
        return Result.Success();
    }

    public Result ValidateStructure()
    {
        if (_sections.Count == 0)
            return Result.Failure(new Error("SeatingLayout.NoSections", "Layout must contain at least one section."));

        if (_rows.Count == 0)
            return Result.Failure(new Error("SeatingLayout.NoRows", "Layout must contain at least one row."));

        if (_seats.Count == 0)
            return Result.Failure(new Error("SeatingLayout.NoSeats", "Layout must contain at least one seat."));

        if (_priceTiers.Count == 0)
            return Result.Failure(new Error("SeatingLayout.NoPriceTiers", "Layout must contain at least one price tier."));

        return Result.Success();
    }
}
