namespace PulseReserve.SharedKernel;

/// <summary>
/// Classifies an error so callers (e.g. the HTTP layer) can react without parsing error codes.
/// </summary>
public enum ErrorType
{
    /// <summary>The request or input is invalid.</summary>
    Validation = 0,

    /// <summary>The addressed resource does not exist.</summary>
    NotFound = 1,

    /// <summary>The request conflicts with the current state of the resource (e.g. an illegal lifecycle transition).</summary>
    Conflict = 2,

    /// <summary>A required capability is not available (e.g. no payment provider is configured).</summary>
    Unavailable = 3
}

/// <summary>
/// Represents a standardized domain or validation error.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Validation)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static readonly Error NullValue = new("General.NullValue", "The specified value is null.");

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
}
