using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaasRls.Api.Api;
using SaasRls.Tenancy.Context;

namespace SaasRls.Api.Infrastructure;

/// <summary>
/// Maps database errors to problem details. The mapping is deliberately coarse for tenant callers: an RLS violation, a
/// foreign key to a row the caller cannot see and a row that does not exist must all look alike, or the error itself
/// becomes the oracle RLS was meant to remove.
/// </summary>
internal sealed class DatabaseExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // EF Core wraps the database error of a failed SaveChanges.
        if (exception is DbUpdateException { InnerException: PostgresException inner })
        {
            exception = inner;
        }

        var (status, title) = exception switch
        {
            AdminReasonMissingException => (StatusCodes.Status400BadRequest, exception.Message),
            TenantContextMissingException => (StatusCodes.Status403Forbidden, "Tenant required"),
            PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } => (StatusCodes.Status403Forbidden, "Forbidden by tenant isolation"),
            PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } => (StatusCodes.Status422UnprocessableEntity, "A referenced record does not exist"),
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => (StatusCodes.Status409Conflict, "A record with the same key already exists"),
            PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation } => (StatusCodes.Status400BadRequest, "The record violates a constraint"),
            // Raised by the admin audit function: missing operator (28000) or reason (22023), unknown tenant (P0002).
            PostgresException { SqlState: PostgresErrorCodes.InvalidAuthorizationSpecification or PostgresErrorCodes.InvalidParameterValue } pg => (StatusCodes.Status400BadRequest, pg.MessageText),
            PostgresException { SqlState: "P0002" } => (StatusCodes.Status404NotFound, "Not found"),
            PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } => (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable, retry."),
            NpgsqlException { IsTransient: true } or TimeoutException => (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable, retry."),
            _ => (0, null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title },
            Exception = exception,
        });
    }
}
