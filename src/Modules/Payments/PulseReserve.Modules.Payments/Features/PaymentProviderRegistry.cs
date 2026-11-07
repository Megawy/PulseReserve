using PulseReserve.Modules.Payments.Domain;
using PulseReserve.SharedKernel;

namespace PulseReserve.Modules.Payments.Features;

/// <summary>
/// The server-side set of payment providers. Which providers exist, are enabled and may charge a currency is decided
/// only by configuration; a customer can at most NAME one of the enabled providers. Existing attempts and refunds keep
/// the provider they were created with (<see cref="Find"/>), so a provider is never silently swapped after an
/// ambiguous result.
/// </summary>
public interface IPaymentProviderRegistry
{
    /// <summary>Providers that can take a payment right now (enabled and correctly configured).</summary>
    IReadOnlyList<IPaymentProvider> Available { get; }

    /// <summary>Every registered provider, available or not (for operator visibility).</summary>
    IReadOnlyList<IPaymentProvider> All { get; }

    /// <summary>Looks a provider up by the name stored on an attempt or refund; null when it is no longer registered.</summary>
    IPaymentProvider? Find(string name);

    /// <summary>Picks the provider for a NEW attempt: the requested one, or the only available one when none was requested.</summary>
    Result<IPaymentProvider> Select(string? requested, string currency);
}

public sealed class PaymentProviderRegistry : IPaymentProviderRegistry
{
    private readonly Dictionary<string, IPaymentProvider> _byName;

    public PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers)
    {
        // A later registration with the same name replaces an earlier one (host and test overrides).
        _byName = new Dictionary<string, IPaymentProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
            _byName[provider.Name] = provider;
        Available = _byName.Values.Where(p => p.IsAvailable).ToList();
    }

    public static PaymentProviderRegistry Single(IPaymentProvider provider) => new([provider]);

    public IReadOnlyList<IPaymentProvider> Available { get; }

    public IReadOnlyList<IPaymentProvider> All => _byName.Values.ToList();

    public IPaymentProvider? Find(string name) => _byName.GetValueOrDefault(name);

    public Result<IPaymentProvider> Select(string? requested, string currency)
    {
        if (Available.Count == 0)
            return Result.Failure<IPaymentProvider>(PaymentsErrors.ProviderUnavailable);

        IPaymentProvider? chosen;
        if (string.IsNullOrWhiteSpace(requested))
        {
            var eligible = Available.Where(p => p.SupportsCurrency(currency)).ToList();
            if (Available.Count > 1)
                return Result.Failure<IPaymentProvider>(PaymentsErrors.ProviderRequired);
            chosen = eligible.FirstOrDefault();
            return chosen is null
                ? Result.Failure<IPaymentProvider>(PaymentsErrors.CurrencyNotSupported)
                : Result.Success(chosen);
        }

        chosen = Available.FirstOrDefault(p => string.Equals(p.Name, requested.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
            return Result.Failure<IPaymentProvider>(PaymentsErrors.ProviderNotEnabled);
        return chosen.SupportsCurrency(currency)
            ? Result.Success(chosen)
            : Result.Failure<IPaymentProvider>(PaymentsErrors.CurrencyNotSupported);
    }
}
