using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddRow;

public sealed record AddRowCommand(
    SeatingLayoutId LayoutId,
    RowId RowId,
    SectionId SectionId,
    string Label,
    int DisplayOrder,
    int? ExpectedVersion = null);
