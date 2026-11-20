using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monstertacle.Hosting.Contracts;
using Access = Monstertacle.Hosting.ControlPlane.BrokerPolicy.Access;

namespace Monstertacle.Hosting.ControlPlane;

public static class BrokerApplication
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var authentication = new BrokerAuthentication(builder.Configuration);
        authentication.Configure(builder);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<BrokerPolicy>();
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

            await next(context);
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
                try
                {
                    return await next(invocation);
                }
                catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            });

        sessions.MapPost("", Start);
        sessions.MapGet("/{sessionId:guid}", (Guid sessionId, HttpContext context, SessionStore store) =>
            ReadStatus(store.Status(Caller(context), sessionId)));
        sessions.MapGet("/by-mutation/{project}/{mutationId:guid}", (string project, Guid mutationId,
            HttpContext context, SessionStore store) => ReadStatus(store.Lookup(Caller(context), project, mutationId)));
        sessions.MapGet("", List);

        return app;
    }

    private static Access Caller(HttpContext context) => (Access)context.Items[typeof(Access)]!;

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
}
