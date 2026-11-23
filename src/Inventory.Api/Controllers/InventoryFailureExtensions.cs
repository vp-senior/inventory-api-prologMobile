using System.Globalization;
using Inventory.Core.Results;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

internal static class InventoryFailureExtensions
{
    /// <summary>
    /// Translates a failure to obtain inventory data into an RFC 9457 problem response.
    /// This service sits in front of another API, so the gateway status codes say most precisely
    /// whose fault a failure is: none of these is the caller's, so none of them is a 4xx.
    /// </summary>
    public static ObjectResult ToProblem(this ControllerBase controller, InventoryFailure failure)
    {
        var (status, title) = failure.Kind switch
        {
            // Temporary: the upstream is down, failing or rate limiting. Worth trying again later.
            InventoryFailureKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Inventory data is temporarily unavailable"),

            InventoryFailureKind.Timeout => (StatusCodes.Status504GatewayTimeout, "The inventory API did not respond in time"),

            // The upstream refused us (for example a bad API key): this service is misconfigured.
            InventoryFailureKind.Rejected => (StatusCodes.Status502BadGateway, "The inventory API rejected the request"),

            // The upstream answered, but with data we cannot vouch for. No figures are better than wrong figures.
            InventoryFailureKind.InvalidData => (StatusCodes.Status502BadGateway, "The inventory API returned data that failed verification"),

            _ => (StatusCodes.Status500InternalServerError, "Unexpected failure"),
        };

        if (failure.RetryAfter is { } retryAfter)
        {
            var seconds = (long)Math.Ceiling(retryAfter.TotalSeconds);
            controller.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        var extensions = failure.Details.Count > 0
            ? new Dictionary<string, object?> { ["errors"] = failure.Details }
            : null;

        return controller.Problem(detail: failure.Message, statusCode: status, title: title, extensions: extensions);
    }
}
