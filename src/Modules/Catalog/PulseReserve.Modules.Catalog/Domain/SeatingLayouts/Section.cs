using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

public sealed class Section : Entity<SectionId>
{
    internal Section(SectionId id, string name, int displayOrder) : base(id)
    {
        Name = name;
        DisplayOrder = displayOrder;
    }

    public string Name { get; }
    public int DisplayOrder { get; }

    public static Result<Section> Create(SectionId id, string name, int displayOrder)
    {
        if (id == SectionId.Empty)
            return Result.Failure<Section>(new Error("Section.EmptyId", "Section identifier cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Section>(new Error("Section.EmptyName", "Section name cannot be empty."));

        return Result.Success(new Section(id, name.Trim(), displayOrder));
    }
}
