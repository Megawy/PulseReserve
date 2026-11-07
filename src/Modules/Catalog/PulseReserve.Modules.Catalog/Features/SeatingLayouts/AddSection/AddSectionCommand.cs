using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSection;

public sealed record AddSectionCommand(
    SeatingLayoutId LayoutId,
    SectionId SectionId,
    string Name,
    int DisplayOrder,
    int? ExpectedVersion = null);
