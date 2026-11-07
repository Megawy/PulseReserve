using System.Globalization;

namespace PulseReserve.Modules.Payments.Features;

/// <summary>
/// Converts an order total (decimal, major units) to the integer minor units both providers expect, without floating
/// point. Currencies have 0, 2 or 3 decimal places (ISO 4217); an amount that is not representable is refused rather
/// than rounded, so what the customer is charged is exactly what the order says.
/// </summary>
public static class CurrencyAmounts
{
    // Zero-decimal currencies Stripe lists (https://docs.stripe.com/currencies#zero-decimal), three-decimal likewise.
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF"
    };

    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BHD", "JOD", "KWD", "OMR", "TND"
    };

    public static int Exponent(string currency) =>
        ZeroDecimal.Contains(currency) ? 0 : ThreeDecimal.Contains(currency) ? 3 : 2;

    /// <summary>Throws <see cref="ArgumentException"/> when the amount has more precision than the currency allows or is not positive.</summary>
    public static long ToMinorUnits(decimal amount, string currency)
    {
        var scale = (decimal)Math.Pow(10, Exponent(currency));
        var minor = amount * scale;
        if (amount <= 0 || minor != decimal.Truncate(minor))
            throw new ArgumentException($"The amount {amount.ToString(CultureInfo.InvariantCulture)} is not a valid positive {currency.ToUpperInvariant()} charge.", nameof(amount));
        return (long)minor;
    }
}
