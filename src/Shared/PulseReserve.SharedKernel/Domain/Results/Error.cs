namespace PulseReserve.SharedKernel;

/// <summary>
/// Represents a standardized domain or validation error.
/// </summary>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static readonly Error NullValue = new("General.NullValue", "The specified value is null.");
}
