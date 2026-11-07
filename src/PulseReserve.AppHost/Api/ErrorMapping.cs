using PulseReserve.SharedKernel;

namespace PulseReserve.AppHost.Api;

/// <summary>
/// Maps domain errors to HTTP status codes by their explicit <see cref="ErrorType"/> and produces ProblemDetails results.
/// </summary>
public static class ErrorMapping
{
    /// <summary>
    /// Maps a domain <see cref="Error"/> to the appropriate <see cref="IResult"/> ProblemDetails response.
    /// </summary>
    public static IResult ToProblemResult(this Error error, IDictionary<string, object?>? extensions = null)
    {
        return Results.Problem(
            detail: error.Message,
            title: error.Code,
            statusCode: MapToStatusCode(error.Type),
            extensions: extensions);
    }

    public static int MapToStatusCode(ErrorType errorType) => errorType switch
    {
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };
}
