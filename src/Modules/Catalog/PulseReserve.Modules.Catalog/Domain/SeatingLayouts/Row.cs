using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

public sealed class Row : Entity<RowId>
{
    internal Row(RowId id, SectionId sectionId, string label, int displayOrder) : base(id)
    {
        SectionId = sectionId;
        Label = label;
        DisplayOrder = displayOrder;
    }

    public SectionId SectionId { get; }
    public string Label { get; }
    public int DisplayOrder { get; }

    public static Result<Row> Create(RowId id, SectionId sectionId, string label, int displayOrder)
    {
        if (id == RowId.Empty)
            return Result.Failure<Row>(new Error("Row.EmptyId", "Row identifier cannot be empty."));

        if (sectionId == SectionId.Empty)
            return Result.Failure<Row>(new Error("Row.EmptySectionId", "Section identifier cannot be empty."));

        if (string.IsNullOrWhiteSpace(label))
            return Result.Failure<Row>(new Error("Row.EmptyLabel", "Row label cannot be empty."));

        return Result.Success(new Row(id, sectionId, label.Trim(), displayOrder));
    }
}
