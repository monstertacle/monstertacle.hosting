using Monstertacle.Hosting.Security;
using Microsoft.Data.Sqlite;
using Monstertacle.Hosting.Contracts;
using Access = Monstertacle.Hosting.Security.BrokerPolicy.Access;
using Worker = Monstertacle.Hosting.ControlPlane.WorkerRegistrations.WorkerIdentity;

namespace Monstertacle.Hosting.ControlPlane;

public static class BrokerApplication
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var authentication = new BrokerAuthentication(builder.Configuration);
        authentication.Configure(builder);
        builder.Services.AddSingleton(new BrokerPolicy(builder.Configuration.GetSection("Broker")));
        builder.Services.AddSingleton<SessionStore>();

        var app = builder.Build();
        _ = app.Services.GetRequiredService<SessionStore>();
        var policy = app.Services.GetRequiredService<BrokerPolicy>();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(SessionContract.Route))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!authentication.IsEdgeRequest(context))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            else if (context.Request.Path.StartsWithSegments(WorkerContract.Route))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (authentication.Worker(context) is not { } worker)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                context.Items[typeof(Worker)] = worker;
            }

            try
            {
                await next(context);
            }
            catch (SqliteException error) when ((error.SqliteErrorCode is 5 or 6) && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health/live", () => Results.NoContent());

        var sessions = app.MapGroup(SessionContract.Route)
            .RequireAuthorization()
            .AddEndpointFilter(async (invocation, next) =>
            {
                var context = invocation.HttpContext;
                if (policy.Resolve(context.User) is not { } access)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                context.Items[typeof(Access)] = access;
                return await next(invocation);
            });

        sessions.MapPost("", Start);
        sessions.MapGet("/{sessionId:guid}", (Guid sessionId, HttpContext context, SessionStore store) =>
            ReadStatus(store.Status(Caller(context), sessionId)));
        sessions.MapGet("/by-mutation/{project}/{mutationId:guid}", (string project, Guid mutationId,
            HttpContext context, SessionStore store) => ReadStatus(store.Lookup(Caller(context), project, mutationId)));
        sessions.MapGet("", List);
        sessions.MapPost("/{sessionId:guid}/cancel", Cancel);

        var workers = app.MapGroup(WorkerContract.Route);
        workers.MapPost("/claims", (ClaimJob request, HttpContext context, SessionStore store) =>
        {
            if (request.MutationId == Guid.Empty)
            {
                return Results.BadRequest();
            }

            var claim = store.Claim(CurrentWorker(context), request.MutationId, policy);
            return claim is null ? Results.NoContent() : Results.Ok(claim);
        });
        workers.MapGet("/jobs/{jobId:guid}/claims/{fence:guid}", (Guid jobId, Guid fence, HttpContext context,
            SessionStore store) => store.Observe(CurrentWorker(context), jobId, fence) is { } claim
                ? Results.Ok(claim)
                : Results.NotFound());
        workers.MapPost("/jobs/{jobId:guid}/claims/{fence:guid}/dispatch", (Guid jobId, Guid fence,
            HttpContext context, SessionStore store) => store.AuthorizeDispatch(CurrentWorker(context), jobId, fence, policy) switch
            {
                { Outcome: SessionStore.DispatchOutcome.Authorized, Dispatch: var dispatch } => Results.Ok(dispatch),
                { Outcome: SessionStore.DispatchOutcome.Denied } => Results.StatusCode(StatusCodes.Status403Forbidden),
                { Outcome: SessionStore.DispatchOutcome.Unavailable } => Results.Conflict(),
                _ => Results.NotFound()
            });
        workers.MapPost("/jobs/{jobId:guid}/claims/{fence:guid}/observation", (Guid jobId, Guid fence,
            JobObservation observation, HttpContext context, SessionStore store) =>
            {
                if (!observation.IsValid())
                {
                    return Results.BadRequest();
                }

                return store.RecordObservation(CurrentWorker(context), jobId, fence, observation) switch
                {
                    SessionStore.ObservationOutcome.Accepted => Results.NoContent(),
                    SessionStore.ObservationOutcome.Conflict => Results.Conflict(),
                    _ => Results.NotFound()
                };
            });

        workers.MapPost("/jobs/{jobId:guid}/claims/{fence:guid}/result", (Guid jobId, Guid fence,
            JobCompletion completion, HttpContext context, SessionStore store) =>
            store.Complete(CurrentWorker(context), jobId, fence, completion) switch
            {
                { Outcome: SessionStore.ResultOutcome.Accepted, Result: var result } => Results.Ok(result),
                { Outcome: SessionStore.ResultOutcome.Invalid } => Results.BadRequest(),
                { Outcome: SessionStore.ResultOutcome.Conflict } => Results.Conflict(),
                _ => Results.NotFound()
            });

        return app;
    }

    private static Access Caller(HttpContext context) => (Access)context.Items[typeof(Access)]!;
    private static Worker CurrentWorker(HttpContext context) => (Worker)context.Items[typeof(Worker)]!;

    private static IResult Start(StartSession request, HttpContext context, SessionStore store)
    {
        if (!request.IsValid())
        {
            return Results.BadRequest();
        }

        var admission = store.Start(Caller(context), request);
        return admission.Outcome switch
        {
            SessionStore.AdmissionOutcome.Created =>
                Results.Created($"{SessionContract.Route}/{admission.Session!.SessionId:D}", admission.Session),
            SessionStore.AdmissionOutcome.Replayed => Results.Ok(admission.Session),
            SessionStore.AdmissionOutcome.Conflict => Results.Conflict(),
            SessionStore.AdmissionOutcome.Capacity => Results.StatusCode(StatusCodes.Status429TooManyRequests),
            _ => Results.StatusCode(StatusCodes.Status403Forbidden)
        };
    }

    private static IResult List(string project, Guid? after, int? limit, HttpContext context, SessionStore store)
    {
        if (limit is < 1 or > 50)
        {
            return Results.BadRequest();
        }

        var access = Caller(context);
        return access.HasGrant(project, "read")
            ? Results.Ok(store.List(access, project, after, limit ?? 20))
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static IResult ReadStatus(SessionStatus? status) =>
        status is null ? Results.NotFound() : Results.Ok(status);

    private static IResult Cancel(Guid sessionId, CancelSession request, HttpContext context, SessionStore store)
    {
        if (request.MutationId == Guid.Empty)
        {
            return Results.BadRequest();
        }

        return store.Cancel(Caller(context), sessionId, request.MutationId) switch
        {
            { Outcome: SessionStore.CancelOutcome.Accepted, Session: var status } => Results.Ok(status),
            { Outcome: SessionStore.CancelOutcome.Conflict } => Results.Conflict(),
            _ => Results.NotFound()
        };
    }
}
