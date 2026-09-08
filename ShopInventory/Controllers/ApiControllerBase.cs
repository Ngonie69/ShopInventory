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
            ErrorType.Failure => StatusCodes.Status400BadRequest,
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

    /// <summary>
    /// Hangs the error code, and the full list with each error's code and type, off the problem
    /// details as extension members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is named <c>errorDetails</c> and not <c>errors</c> because <c>errors</c> is already
    /// taken. <see cref="ValidationProblemDetails"/> serialises its own dictionary under that JSON
    /// name, and <see cref="ProblemDetails.Extensions"/> is <c>[JsonExtensionData]</c>, so writing
    /// <c>errors</c> here put the key in the response twice — the RFC 9457 dictionary and then an
    /// array of a different shape — on every endpoint returning an all-Validation
    /// <see cref="ErrorOr"/> result.
    /// </para>
    /// <para>
    /// That was not merely untidy. A strict parser may reject a duplicated key outright, and
    /// <see cref="System.Text.Json.JsonDocument"/>, which tolerates it, resolved
    /// <c>TryGetProperty("errors")</c> to the second one — so every reader in ShopInventory.Web
    /// written against the dictionary either skipped it on a <c>ValueKind</c> guard or read the
    /// array instead, and the user was shown "Code; Description; Validation" where the sentence
    /// should have been.
    /// </para>
    /// <para>
    /// The dictionary is the contract: it is the ASP.NET and RFC 9457 shape, it is the only shape
    /// API.md documents, and it is what the Web already reads. This follows
    /// <c>ValidationExceptionHandler</c>, which for the same reason hangs its own side-channel off
    /// <c>errorCodes</c> rather than crowding <c>errors</c>.
    /// </para>
    /// </remarks>
    private static void AddErrorExtensions(ProblemDetails problemDetails, List<Error> errors)
    {
        if (errors.Count == 0)
        {
            return;
        }

        problemDetails.Extensions["code"] = errors[0].Code;
        problemDetails.Extensions["errorDetails"] = errors
            .Select(error => new
            {
                error.Code,
                error.Description,
                Type = error.Type.ToString()
            })
            .ToArray();
    }
}
