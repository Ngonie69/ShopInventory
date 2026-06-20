using Asp.Versioning;
using ErrorOr;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ShopInventory.Common.ProblemDetails;
using ApiProblemDetails = ShopInventory.Common.ProblemDetails.ProblemDetailsDefaults;

namespace ShopInventory.Controllers;

[ApiController]
[ApiVersion("1.0")]
public class ApiControllerBase : ControllerBase
{
    protected IActionResult Problem(List<Error> errors)
    {
        if (errors.Count == 0)
        {
            return CreateProblemResult(
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred.",
                "The request could not be completed. Use the traceId when reviewing server logs.",
                []);
        }

        if (errors.All(e => e.Type == ErrorType.Validation))
        {
            var modelState = new ModelStateDictionary();
            foreach (var error in errors)
                modelState.AddModelError(error.Code, error.Description);

            var problemDetails = new ValidationProblemDetails(modelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
                Type = ApiProblemDetails.GetType(StatusCodes.Status400BadRequest),
                Detail = "The request contains validation errors."
            };
            AddErrorExtensions(problemDetails, errors);
            ApiProblemDetails.Apply(HttpContext, problemDetails);

            return new BadRequestObjectResult(problemDetails)
            {
                ContentTypes = { "application/problem+json" }
            };
        }

        var firstError = errors[0];

        var statusCode = firstError.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.Unauthorized => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };

        var title = statusCode >= StatusCodes.Status500InternalServerError
            ? "An unexpected error occurred."
            : firstError.Description;

        var detail = statusCode >= StatusCodes.Status500InternalServerError
            ? "The request could not be completed. Use the traceId when reviewing server logs."
            : firstError.Description;

        return CreateProblemResult(statusCode, title, detail, errors);
    }

    private ObjectResult CreateProblemResult(
        int statusCode,
        string title,
        string detail,
        List<Error> errors)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = ApiProblemDetails.GetType(statusCode),
            Detail = detail
        };
        AddErrorExtensions(problemDetails, errors);
        ApiProblemDetails.Apply(HttpContext, problemDetails);

        return new ObjectResult(problemDetails)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" }
        };
    }

    private static void AddErrorExtensions(ProblemDetails problemDetails, List<Error> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        problemDetails.Extensions["code"] = errors[0].Code;
        problemDetails.Extensions["errors"] = errors
            .Select(error => new
            {
                error.Code,
                error.Description,
                Type = error.Type.ToString()
            })
            .ToArray();
    }
}
