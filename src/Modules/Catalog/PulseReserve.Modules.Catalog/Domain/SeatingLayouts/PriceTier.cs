using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.SeatingLayouts;

public sealed class PriceTier : Entity<PriceTierId>
{
    internal PriceTier(PriceTierId id, string name) : base(id)
    {
        Name = name;
    }

    public string Name { get; private set; }

    public static Result<PriceTier> Create(PriceTierId id, string name)
    {
        if (id == PriceTierId.Empty)
            return Result.Failure<PriceTier>(new Error("PriceTier.EmptyId", "Price tier identifier cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<PriceTier>(new Error("PriceTier.EmptyName", "Price tier name cannot be empty."));

        return Result.Success(new PriceTier(id, name.Trim()));
    }
}
