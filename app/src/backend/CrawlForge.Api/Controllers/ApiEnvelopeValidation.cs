using System.Text.Json;
using CrawlForge.Contracts.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Uniform-envelope client-error conventions (FS-CRAWL-001 §10.1/§10.4,
/// defect D8): the framework's automatic <c>[ApiController]</c> validation and
/// client-error mapping must never emit RFC 9110 ProblemDetails on the locked
/// REST surface. Model-state failures become the envelope validation shape
/// (error.code="validation", fieldErrors as a field→reasons map through the
/// locked <see cref="ApiErrorConverter"/>); any remaining framework
/// ProblemDetails (e.g. 415 unsupported media type) are rewritten into the
/// envelope by an always-run result filter.
/// </summary>
public static class ApiEnvelopeValidation
{
    /// <summary>
    /// Applies the envelope client-error conventions to an MVC builder. Used
    /// by the Host composition and the controller test host so both surfaces
    /// behave identically.
    /// </summary>
    /// <param name="builder">The MVC builder.</param>
    /// <returns>The same builder for chaining.</returns>
    public static IMvcBuilder AddEnvelopeValidation(this IMvcBuilder builder)
    {
        builder.ConfigureApiBehaviorOptions(options =>
            options.InvalidModelStateResponseFactory = CreateValidationResult);
        builder.AddMvcOptions(options => options.Filters.Add<EnvelopeClientErrorFilter>());
        return builder;
    }

    /// <summary>
    /// Builds the locked 400 validation envelope from model-state errors:
    /// one field→reasons entry per error, wire field names camelCase.
    /// </summary>
    private static IActionResult CreateValidationResult(ActionContext context)
    {
        var fieldErrors = new List<ApiFieldError>();
        foreach (var (key, entry) in context.ModelState)
        {
            foreach (var error in entry.Errors)
            {
                var reason = string.IsNullOrEmpty(error.ErrorMessage)
                    ? "The supplied value is invalid."
                    : error.ErrorMessage;
                fieldErrors.Add(new ApiFieldError(NormalizeField(key), reason));
            }
        }

        return new BadRequestObjectResult(new ApiEnvelope<object?>(
            null, null,
            new ApiError(
                ApiErrorCodes.Validation,
                "One or more request fields are invalid.",
                fieldErrors.Count > 0 ? fieldErrors : null)));
    }

    /// <summary>
    /// Normalizes a model-state key into a wire field name: JSON binding
    /// paths lose their "$." root prefix; names are camelCased.
    /// </summary>
    private static string NormalizeField(string key)
    {
        var field = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        return field.Length == 0 ? "$" : JsonNamingPolicy.CamelCase.ConvertName(field);
    }

    /// <summary>Maps an HTTP status onto the stable envelope error code set.</summary>
    internal static string CodeForStatus(int? status) => status switch
    {
        StatusCodes.Status400BadRequest => ApiErrorCodes.Validation,
        StatusCodes.Status404NotFound => ApiErrorCodes.NotFound,
        StatusCodes.Status409Conflict => ApiErrorCodes.Conflict,
        StatusCodes.Status415UnsupportedMediaType => ApiErrorCodes.Unsupported,
        _ => ApiErrorCodes.Internal,
    };
}

/// <summary>
/// Always-run result filter that rewrites any framework ProblemDetails result
/// (e.g. the 415 produced by input-formatter negotiation) into the locked
/// uniform envelope. Controller-produced envelopes pass through untouched.
/// </summary>
public sealed class EnvelopeClientErrorFilter : IAlwaysRunResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult objectResult || objectResult.Value is not ProblemDetails problem)
        {
            return;
        }

        var message = problem.Detail ?? problem.Title ?? "The request could not be processed.";
        ApiError error;
        if (problem is ValidationProblemDetails validation && validation.Errors.Count > 0)
        {
            var fieldErrors = new List<ApiFieldError>();
            foreach (var (field, reasons) in validation.Errors)
            {
                foreach (var reason in reasons)
                {
                    fieldErrors.Add(new ApiFieldError(field, reason));
                }
            }

            error = new ApiError(ApiErrorCodes.Validation, message, fieldErrors);
        }
        else
        {
            error = new ApiError(ApiEnvelopeValidation.CodeForStatus(problem.Status), message);
        }

        objectResult.Value = new ApiEnvelope<object?>(null, null, error);
        objectResult.DeclaredType = typeof(ApiEnvelope<object?>);
        objectResult.ContentTypes.Clear();
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
        // No post-execution work.
    }
}
