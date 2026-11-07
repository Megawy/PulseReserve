namespace PulseReserve.AppHost.Api;

// ──────────────── Venue Requests ────────────────

public sealed record CreateVenueApiRequest(
    string Name,
    string Street,
    string City,
    string Country,
    string PostalCode);

// ──────────────── SeatingLayout Requests ────────────────

public sealed record CreateSeatingLayoutApiRequest(
    Guid VenueId,
    string Name);

public sealed record AddSectionApiRequest(
    string Name,
    int DisplayOrder,
    int? ExpectedVersion = null);

public sealed record AddRowApiRequest(
    Guid SectionId,
    string Label,
    int DisplayOrder,
    int? ExpectedVersion = null);

public sealed record AddSeatApiRequest(
    Guid RowId,
    Guid SectionId,
    string Label,
    int CoordinateX,
    int CoordinateY,
    Guid PriceTierId,
    int? ExpectedVersion = null);

public sealed record AddPriceTierApiRequest(
    string Name,
    int? ExpectedVersion = null);

public sealed record LockSeatingLayoutApiRequest(
    int? ExpectedVersion = null);

// ──────────────── Event Requests ────────────────

public sealed record CreateEventApiRequest(
    string Name,
    string Description,
    Guid VenueId,
    DateTime ScheduledStartUtc,
    DateTime ScheduledEndUtc,
    Guid? SeatingLayoutId = null);

public sealed record SetEventPriceApiRequest(
    Guid PriceTierId,
    decimal PriceAmount,
    string Currency,
    int? ExpectedVersion = null);

public sealed record PublishEventApiRequest(
    int? ExpectedVersion = null);

public sealed record StartSaleApiRequest(
    int? ExpectedEventVersion = null,
    int? ExpectedLayoutVersion = null);

// ──────────────── Lightweight ID Response ────────────────

public sealed record CreatedIdResponse(Guid Id);
