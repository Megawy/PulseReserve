using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using PulseReserve.AppHost.Api;
using PulseReserve.Modules.Catalog.Contracts;
using PulseReserve.Modules.Catalog.Domain.Events;
using PulseReserve.SharedKernel;
using Xunit;

namespace PulseReserve.UnitTests.Api;

// HTTP status mapping is driven by the explicit ErrorType, never by parsing error codes.
public class ErrorMappingTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string, Func<Error>> LifecycleConflicts => new()
    {
        // Reachable over HTTP (also covered end-to-end in CatalogApiHttpTests).
        { "Event.AlreadyPublished", () => PublishedEvent().Publish(Now).Error },
        { "Event.NotPublished", () => DraftEvent().StartSale(Now).Error },

        // No HTTP route exists for Close/Cancel yet; the errors are produced by the real domain methods.
        { "Event.TerminalStatus", () => CancelledEvent().Publish(Now).Error },
        { "Event.NotOnSale", () => DraftEvent().Close(Now).Error },
        { "Event.AlreadyFinished", () => CancelledEvent().Cancel("Again", Now).Error }
    };

    [Theory]
    [MemberData(nameof(LifecycleConflicts))]
    public void LifecycleTransitionErrors_AreConflicts_AndMapTo409(string expectedCode, Func<Error> produceError)
    {
        var error = produceError();

        error.Code.Should().Be(expectedCode);
        error.Type.Should().Be(ErrorType.Conflict);
        ProblemFor(error).StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    public void ErrorType_MapsToExpectedStatus(ErrorType type, int expectedStatus)
    {
        ErrorMapping.MapToStatusCode(type).Should().Be(expectedStatus);
    }

    [Fact]
    public void ErrorsWithNotFoundInTheirCode_ButValidationType_MapTo400()
    {
        // Regression: substring matching previously turned request-body reference errors into 404.
        var error = new Error("SeatingLayout.ParentSectionNotFound", "Parent section does not exist in this layout.");

        error.Type.Should().Be(ErrorType.Validation);
        ProblemFor(error).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void ProblemResult_CarriesCodeAsTitle_AndMessageAsDetail()
    {
        var problem = ProblemFor(Error.NotFound("Event.NotFound", "Event was not found."));

        problem.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        problem.ProblemDetails.Title.Should().Be("Event.NotFound");
        problem.ProblemDetails.Detail.Should().Be("Event was not found.");
    }

    [Fact]
    public void ErrorConstructor_DefaultsToValidation()
    {
        new Error("Any.Code", "Message").Type.Should().Be(ErrorType.Validation);
        Error.None.Type.Should().Be(ErrorType.Validation);
    }

    private static ProblemHttpResult ProblemFor(Error error) =>
        error.ToProblemResult().Should().BeOfType<ProblemHttpResult>().Subject;

    private static Event DraftEvent()
    {
        var evt = Event.CreateDraft(EventId.New(), "Mapping", "Test", VenueId.New(), Now.AddDays(1), Now.AddDays(2)).Value;
        evt.AssignSeatingLayout(SeatingLayoutId.New());
        return evt;
    }

    private static Event PublishedEvent()
    {
        var evt = DraftEvent();
        evt.Publish(Now).IsSuccess.Should().BeTrue();
        return evt;
    }

    private static Event CancelledEvent()
    {
        var evt = DraftEvent();
        evt.Cancel("Setup", Now).IsSuccess.Should().BeTrue();
        return evt;
    }
}
