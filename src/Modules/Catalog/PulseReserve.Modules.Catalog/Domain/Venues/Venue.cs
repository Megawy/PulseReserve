using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.ValueObjects;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.Venues;

public sealed class Venue : AggregateRoot<VenueId>
{
    private Venue(VenueId id, string name, Address address) : base(id)
    {
        Name = name;
        Address = address;
    }

    public string Name { get; private set; }
    public Address Address { get; private set; }

    public static Result<Venue> Create(VenueId id, string name, Address address)
    {
        if (id == VenueId.Empty)
            return Result.Failure<Venue>(new Error("Venue.EmptyId", "Venue identifier cannot be empty."));

        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Venue>(new Error("Venue.EmptyName", "Venue name cannot be empty."));

        if (address is null)
            return Result.Failure<Venue>(new Error("Venue.NullAddress", "Venue address is required."));

        return Result.Success(new Venue(id, name.Trim(), address));
    }

    public Result UpdateDetails(string name, Address address)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(new Error("Venue.EmptyName", "Venue name cannot be empty."));

        if (address is null)
            return Result.Failure(new Error("Venue.NullAddress", "Venue address is required."));

        Name = name.Trim();
        Address = address;
        return Result.Success();
    }
}
