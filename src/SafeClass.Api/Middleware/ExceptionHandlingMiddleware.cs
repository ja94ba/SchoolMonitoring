using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SafeClass.Application.Exceptions;

namespace SafeClass.Api.Middleware;

/// <summary>
/// Turns exceptions into RFC 7807 problem responses. Domain errors keep their (safe) messages;
/// anything unexpected is logged with a trace id and answered with a generic message, never a stack trace.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499; // client closed the request
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                log.LogError(ex, "Unhandled exception after the response started");
                throw;
            }
            await WriteProblemAsync(context, ex);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception ex)
    {
        ProblemDetails problem;
        switch (ex)
        {
            case AppValidationException v:
                problem = new ValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value)) { Status = 400, Title = "Validation failed" };
                break;
            case NotFoundException:
                problem = new ProblemDetails { Status = 404, Title = "Not found", Detail = ex.Message };
                break;
            case ConflictException:
                problem = new ProblemDetails { Status = 409, Title = "Conflict", Detail = ex.Message };
                break;
            case ForbiddenException:
                problem = new ProblemDetails { Status = 403, Title = "Forbidden", Detail = ex.Message };
                break;
            case UnauthorizedAppException:
                problem = new ProblemDetails { Status = 401, Title = "Unauthorized", Detail = ex.Message };
                break;
            case BadHttpRequestException:
                problem = new ProblemDetails { Status = 400, Title = "Bad request", Detail = "The request could not be read." };
                break;
            default:
                log.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path.Value);
                problem = new ProblemDetails { Status = 500, Title = "Server error", Detail = "An unexpected error occurred." };
                break;
        }

        problem.Extensions["traceId"] = context.TraceIdentifier;
        context.Response.StatusCode = problem.Status ?? 500;
        await context.Response.WriteAsJsonAsync<object>(problem, (JsonSerializerOptions?)null, "application/problem+json");
    }
}
