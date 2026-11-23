using Inventory.Api.Contracts;
using Inventory.Core;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

[ApiController]
[Route("api/inventory")]
public sealed class InventoryController(InventoryReportService reports) : ControllerBase
{
    /// <summary>
    /// Inventory aggregated by category: total value, available record count, most recently
    /// updated record, and the records those figures were computed from.
    /// </summary>
    [HttpGet("categories")]
    [ProducesResponseType<InventoryCategoriesResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout, "application/problem+json")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var report = await reports.GetReportAsync(cancellationToken);

        return report.IsSuccess
            ? Ok(InventoryCategoriesResponse.From(report.Value))
            : this.ToProblem(report.Failure);
    }
}
