using PulseReserve.Modules.Catalog.Contracts;

namespace PulseReserve.Modules.Catalog.Features.SeatingLayouts.LockSeatingLayout;

public sealed record LockSeatingLayoutCommand(
    SeatingLayoutId LayoutId,
    int? ExpectedVersion = null);
