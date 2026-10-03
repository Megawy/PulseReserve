namespace PulseReserve.Modules.Catalog.Contracts;

public sealed record SeatMetadataDto(
    SeatId SeatId,
    SectionId SectionId,
    string SectionName,
    RowId RowId,
    string RowLabel,
    string SeatLabel,
    int CoordinateX,
    int CoordinateY,
    PriceTierId PriceTierId,
    string PriceTierName,
    decimal BasePrice,
    string Currency);
