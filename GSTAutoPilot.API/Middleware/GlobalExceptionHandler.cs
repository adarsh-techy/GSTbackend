using System.Net;
using GSTAutoPilot.Application.Exceptions;
using GSTAutoPilot.Domain.DTOs.Common;
using GSTAutoPilot.Infrastructure.Services.WhiteBooksGst;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GSTAutoPilot.API.Middleware;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception occurred: {Message}", exception.Message);

        (int statusCode, string message) = exception switch
        {
            UnauthorizedAccessException ex => ((int)HttpStatusCode.Unauthorized, ex.Message),
            KeyNotFoundException ex => ((int)HttpStatusCode.NotFound, ex.Message),
            ArgumentException ex => ((int)HttpStatusCode.BadRequest, ex.Message),
            InvalidOperationException ex => ((int)HttpStatusCode.BadRequest, ex.Message),
            GstnNotConnectedException ex => ((int)HttpStatusCode.BadRequest, ex.Message),
            NilReturnConfirmationException ex => ((int)HttpStatusCode.Conflict, ex.Message),
            Gstr1UnreportedInvoicesException ex => ((int)HttpStatusCode.BadRequest, ex.Message),
            GstnApiException ex => (ex.HttpStatus ?? (int)HttpStatusCode.BadGateway, ex.Message),
            _ => (
                (int)HttpStatusCode.InternalServerError,
                _env.IsDevelopment() ? exception.Message : "An unexpected error occurred. Please try again later."
            )
        };

        var errors = _env.IsDevelopment() && exception.StackTrace != null
            ? new List<string> { exception.StackTrace }
            : null;

        var response = ApiResponse<object>.Fail(message, errors);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

        return true;
    }
}
