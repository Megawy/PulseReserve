using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Infrastructure.Persistence.Catalog.Converters;

public sealed class VenueIdConverter() : ValueConverter<VenueId, Guid>(
    id => id.Value,
    value => new VenueId(value));

public sealed class SeatingLayoutIdConverter() : ValueConverter<SeatingLayoutId, Guid>(
    id => id.Value,
    value => new SeatingLayoutId(value));

public sealed class SectionIdConverter() : ValueConverter<SectionId, Guid>(
    id => id.Value,
    value => new SectionId(value));

public sealed class RowIdConverter() : ValueConverter<RowId, Guid>(
    id => id.Value,
    value => new RowId(value));

public sealed class SeatIdConverter() : ValueConverter<SeatId, Guid>(
    id => id.Value,
    value => new SeatId(value));

public sealed class PriceTierIdConverter() : ValueConverter<PriceTierId, Guid>(
    id => id.Value,
    value => new PriceTierId(value));

public sealed class EventIdConverter() : ValueConverter<EventId, Guid>(
    id => id.Value,
    value => new EventId(value));
