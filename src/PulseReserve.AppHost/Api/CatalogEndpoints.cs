using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using PulseReserve.AppHost.Security;
using Microsoft.EntityFrameworkCore;
using PulseReserve.Modules.Catalog.Contracts;
using EventId = PulseReserve.Modules.Catalog.Contracts.EventId;
using PulseReserve.Modules.Catalog.Features.Events.CreateEvent;
using PulseReserve.Modules.Catalog.Features.Events.GetEventById;
using PulseReserve.Modules.Catalog.Features.Events.ListEvents;
using PulseReserve.Modules.Catalog.Features.Events.PublishEvent;
using PulseReserve.Modules.Catalog.Features.Events.SetEventPrice;
using PulseReserve.Modules.Catalog.Features.Events.StartSale;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddPriceTier;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddRow;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSeat;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.AddSection;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.CreateSeatingLayout;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.GetSeatingLayoutById;
using PulseReserve.Modules.Catalog.Features.SeatingLayouts.LockSeatingLayout;
using PulseReserve.Modules.Catalog.Features.Venues.CreateVenue;
using PulseReserve.Modules.Catalog.Features.Venues.GetVenueById;

namespace PulseReserve.AppHost.Api;

/// <summary>
/// Registers all Catalog HTTP API endpoints as Minimal‑API route groups.
/// This is a thin adapter — no business logic lives here.
/// </summary>
public static class CatalogEndpoints
{
    public static WebApplication MapCatalogApi(this WebApplication app)
    {
        var catalog = app.MapGroup("/api/catalog")
            .WithTags("Catalog");

        // Route classification (fail-closed): every GET under /api/catalog is a public read (venues, layouts, event
        // list, event details). Every other method is an Admin-only mutation, enforced here by convention so a
        // newly added write route is protected by default. Authorization runs before the CSRF filter: anonymous
        // callers get 401, signed-in non-admins 403, and only then is the antiforgery token checked.
        ((IEndpointConventionBuilder)catalog).Add(builder =>
        {
            var methods = builder.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods).ToList();
            var readOnly = methods.Count > 0 && methods.All(m => HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m));
            if (readOnly)
                return;

            builder.Metadata.Add(new AuthorizeAttribute(AuthPolicies.Admin));
            builder.FilterFactories.Add((_, next) => ctx =>
                ctx.HttpContext.RequestServices.GetRequiredService<CsrfValidationFilter>().InvokeAsync(ctx, next));
        });

        MapVenueEndpoints(catalog);
        MapSeatingLayoutEndpoints(catalog);
        MapEventEndpoints(catalog);

        return app;
    }

    // ═══════════════════════════════════════════════════════════════
    //  VENUES
    // ═══════════════════════════════════════════════════════════════

    private static void MapVenueEndpoints(RouteGroupBuilder group)
    {
        var venues = group.MapGroup("/venues").WithTags("Venues");

        venues.MapPost("/", async (
            [FromBody] CreateVenueApiRequest request,
            CreateVenueHandler handler,
            CancellationToken ct) =>
        {
            var id = VenueId.New();
            var command = new CreateVenueCommand(
                id, request.Name, request.Street, request.City, request.Country, request.PostalCode);

            var result = await handler.HandleAsync(command, ct);

            return result.IsSuccess
                ? Results.Created($"/api/catalog/venues/{result.Value.Value}", new CreatedIdResponse(result.Value.Value))
                : result.Error.ToProblemResult();
        })
        .WithName("CreateVenue")
        .Produces<CreatedIdResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        venues.MapGet("/{venueId:guid}", async (
            Guid venueId,
            GetVenueByIdHandler handler,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new GetVenueByIdQuery(new VenueId(venueId)), ct);

            return result.IsSuccess
                ? Results.Ok(result.Value)
                : result.Error.ToProblemResult();
        })
        .WithName("GetVenueById")
        .Produces<VenueResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SEATING LAYOUTS
    // ═══════════════════════════════════════════════════════════════

    private static void MapSeatingLayoutEndpoints(RouteGroupBuilder group)
    {
        var layouts = group.MapGroup("/seating-layouts").WithTags("SeatingLayouts");

        // ── Create ──
        layouts.MapPost("/", async (
            [FromBody] CreateSeatingLayoutApiRequest request,
            CreateSeatingLayoutHandler handler,
            CancellationToken ct) =>
        {
            var id = SeatingLayoutId.New();
            var command = new CreateSeatingLayoutCommand(id, new VenueId(request.VenueId), request.Name);

            var result = await handler.HandleAsync(command, ct);

            return result.IsSuccess
                ? Results.Created($"/api/catalog/seating-layouts/{result.Value.Value}", new CreatedIdResponse(result.Value.Value))
                : result.Error.ToProblemResult();
        })
        .WithName("CreateSeatingLayout")
        .Produces<CreatedIdResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // ── Get by ID ──
        layouts.MapGet("/{layoutId:guid}", async (
            Guid layoutId,
            GetSeatingLayoutByIdHandler handler,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(
                new GetSeatingLayoutByIdQuery(new SeatingLayoutId(layoutId)), ct);

            return result.IsSuccess
                ? Results.Ok(result.Value)
                : result.Error.ToProblemResult();
        })
        .WithName("GetSeatingLayoutById")
        .Produces<SeatingLayoutResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // ── Add Section ──
        layouts.MapPost("/{layoutId:guid}/sections", async (
            Guid layoutId,
            [FromBody] AddSectionApiRequest request,
            AddSectionHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new AddSectionCommand(
                    new SeatingLayoutId(layoutId),
                    SectionId.New(),
                    request.Name,
                    request.DisplayOrder,
                    request.ExpectedVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The seating layout was modified by another request. Retry with the latest version.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("AddSection")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // ── Add Row ──
        layouts.MapPost("/{layoutId:guid}/rows", async (
            Guid layoutId,
            [FromBody] AddRowApiRequest request,
            AddRowHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new AddRowCommand(
                    new SeatingLayoutId(layoutId),
                    RowId.New(),
                    new SectionId(request.SectionId),
                    request.Label,
                    request.DisplayOrder,
                    request.ExpectedVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The seating layout was modified by another request. Retry with the latest version.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("AddRow")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // ── Add Seat ──
        layouts.MapPost("/{layoutId:guid}/seats", async (
            Guid layoutId,
            [FromBody] AddSeatApiRequest request,
            AddSeatHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new AddSeatCommand(
                    new SeatingLayoutId(layoutId),
                    SeatId.New(),
                    new RowId(request.RowId),
                    new SectionId(request.SectionId),
                    request.Label,
                    request.CoordinateX,
                    request.CoordinateY,
                    new PriceTierId(request.PriceTierId),
                    request.ExpectedVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The seating layout was modified by another request. Retry with the latest version.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("AddSeat")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // ── Add PriceTier ──
        layouts.MapPost("/{layoutId:guid}/price-tiers", async (
            Guid layoutId,
            [FromBody] AddPriceTierApiRequest request,
            AddPriceTierHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new AddPriceTierCommand(
                    new SeatingLayoutId(layoutId),
                    PriceTierId.New(),
                    request.Name,
                    request.ExpectedVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The seating layout was modified by another request. Retry with the latest version.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("AddPriceTier")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // ── Lock Layout ──
        layouts.MapPost("/{layoutId:guid}/lock", async (
            Guid layoutId,
            [FromBody] LockSeatingLayoutApiRequest request,
            LockSeatingLayoutHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new LockSeatingLayoutCommand(
                    new SeatingLayoutId(layoutId),
                    request.ExpectedVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The seating layout was modified by another request. Retry with the latest version.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("LockSeatingLayout")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EVENTS
    // ═══════════════════════════════════════════════════════════════

    private static void MapEventEndpoints(RouteGroupBuilder group)
    {
        var events = group.MapGroup("/events").WithTags("Events");

        // ── Create ──
        events.MapPost("/", async (
            [FromBody] CreateEventApiRequest request,
            CreateEventHandler handler,
            CancellationToken ct) =>
        {
            var id = EventId.New();
            SeatingLayoutId? layoutId = request.SeatingLayoutId.HasValue
                ? new SeatingLayoutId(request.SeatingLayoutId.Value)
                : null;

            var command = new CreateEventCommand(
                id,
                request.Name,
                request.Description,
                new VenueId(request.VenueId),
                request.ScheduledStartUtc,
                request.ScheduledEndUtc,
                layoutId);

            var result = await handler.HandleAsync(command, ct);

            return result.IsSuccess
                ? Results.Created($"/api/catalog/events/{result.Value.Value}", new CreatedIdResponse(result.Value.Value))
                : result.Error.ToProblemResult();
        })
        .WithName("CreateEvent")
        .Produces<CreatedIdResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // ── List (customer-visible events only) ──
        events.MapGet("/", async (
            ListEventsHandler handler,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(ct);
            return Results.Ok(result);
        })
        .WithName("ListEvents")
        .Produces<IReadOnlyList<EventResponse>>();

        // ── Get by ID ──
        events.MapGet("/{eventId:guid}", async (
            Guid eventId,
            GetEventByIdHandler handler,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(new GetEventByIdQuery(new EventId(eventId)), ct);

            return result.IsSuccess
                ? Results.Ok(result.Value)
                : result.Error.ToProblemResult();
        })
        .WithName("GetEventById")
        .Produces<EventResponse>()
        .ProducesProblem(StatusCodes.Status404NotFound);

        // ── Set Price ──
        events.MapPost("/{eventId:guid}/prices", async (
            Guid eventId,
            [FromBody] SetEventPriceApiRequest request,
            SetEventPriceHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new SetEventPriceCommand(
                    new EventId(eventId),
                    new PriceTierId(request.PriceTierId),
                    request.PriceAmount,
                    request.Currency,
                    request.ExpectedVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The event was modified by another request. Retry with the latest version.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("SetEventPrice")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // ── Publish ──
        events.MapPost("/{eventId:guid}/publish", async (
            Guid eventId,
            [FromBody] PublishEventApiRequest request,
            PublishEventHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new PublishEventCommand(
                    new EventId(eventId),
                    request.ExpectedVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The event was modified by another request. Retry with the latest version.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("PublishEvent")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // ── Start Sale ──
        events.MapPost("/{eventId:guid}/start-sale", async (
            Guid eventId,
            [FromBody] StartSaleApiRequest request,
            StartSaleHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var command = new StartSaleCommand(
                    new EventId(eventId),
                    request.ExpectedEventVersion,
                    request.ExpectedLayoutVersion);

                var result = await handler.HandleAsync(command, ct);

                return result.IsSuccess ? Results.Ok() : result.Error.ToProblemResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "The event or its seating layout was modified by another request. Retry with the latest versions.",
                    title: "ConcurrencyConflict",
                    statusCode: StatusCodes.Status409Conflict);
            }
        })
        .WithName("StartSale")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
