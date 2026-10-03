using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Catalog.Domain.ValueObjects;

public sealed class Address : ValueObject
{
    private Address(string street, string city, string country, string postalCode)
    {
        Street = street;
        City = city;
        Country = country;
        PostalCode = postalCode;
    }

    public string Street { get; }
    public string City { get; }
    public string Country { get; }
    public string PostalCode { get; }

    public static Result<Address> Create(string street, string city, string country, string postalCode)
    {
        if (string.IsNullOrWhiteSpace(street))
            return Result.Failure<Address>(new Error("Address.EmptyStreet", "Street cannot be empty."));

        if (string.IsNullOrWhiteSpace(city))
            return Result.Failure<Address>(new Error("Address.EmptyCity", "City cannot be empty."));

        if (string.IsNullOrWhiteSpace(country))
            return Result.Failure<Address>(new Error("Address.EmptyCountry", "Country cannot be empty."));

        return Result.Success(new Address(street.Trim(), city.Trim(), country.Trim(), postalCode?.Trim() ?? string.Empty));
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Street;
        yield return City;
        yield return Country;
        yield return PostalCode;
    }
}
