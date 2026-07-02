using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace GZCTF.Controllers;

[ApiController]
[Route("/error")]
[ApiExplorerSettings(IgnoreApi = true)]
public class ErrorController(IStringLocalizer<Program> localizer) : ControllerBase
{
    [Route("500")]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status500InternalServerError)]
    public Task<IActionResult> InternalServerError()
    {
        // A request body over Kestrel's MaxRequestBodySize surfaces as a
        // BadHttpRequestException (StatusCode 413) during body read / model binding,
        // which UseExceptionHandler otherwise re-executes to here as an opaque 500.
        // Map it back to a real 413 with an actionable message so an oversized upload
        // gets "payload too large" instead of "internal server error".
        var error = HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (error is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            return Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status413PayloadTooLarge,
                new RequestResponse(localizer[nameof(Resources.Program.File_SizeTooLarge)],
                    StatusCodes.Status413PayloadTooLarge)));

        return Task.FromResult<IActionResult>(StatusCode(500,
            new RequestResponse(localizer[nameof(Resources.Program.Error_InternalServerError)],
                StatusCodes.Status500InternalServerError)));
    }
}
