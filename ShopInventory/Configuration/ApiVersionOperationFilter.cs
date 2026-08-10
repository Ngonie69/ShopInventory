using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ShopInventory.Configuration;

/// <summary>
/// Documents the optional <c>api-version</c> query parameter, defaulted to the version of the document
/// the operation appears in.
/// </summary>
/// <remarks>
/// Written against Microsoft.OpenApi 2.x, which moved every model type out of
/// <c>Microsoft.OpenApi.Models</c> into the root namespace, replaced the string <c>Schema.Type</c> with
/// <see cref="JsonSchemaType"/>, and dropped <c>Microsoft.OpenApi.Any</c> in favour of
/// <see cref="JsonNode"/> for literal values.
/// </remarks>
public sealed class ApiVersionOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var apiVersion = GetApiVersion(context);

        operation.Parameters ??= [];

        var existing = operation.Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, "api-version", StringComparison.OrdinalIgnoreCase)
            && parameter.In == ParameterLocation.Query);

        if (existing is null)
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "api-version",
                In = ParameterLocation.Query,
                Required = false,
                Description = "Optional for version 1.0 requests. Supply this when calling a later API version.",
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Default = JsonValue.Create(apiVersion)
                }
            });

            return;
        }

        // 2.x models a parameter as IOpenApiParameter, which a $ref can also satisfy — and a reference
        // has no schema of its own to stamp a default onto. Only the inline kind is touched; a
        // referenced parameter is left as its definition declares it.
        if (existing is not OpenApiParameter inlineParameter)
        {
            return;
        }

        if (inlineParameter.Schema is not OpenApiSchema schema)
        {
            schema = new OpenApiSchema { Type = JsonSchemaType.String };
            inlineParameter.Schema = schema;
        }

        schema.Default = JsonValue.Create(apiVersion);
    }

    private static string GetApiVersion(OperationFilterContext context)
    {
        var groupName = context.ApiDescription.GroupName;
        if (!string.IsNullOrWhiteSpace(groupName) && groupName.StartsWith('v'))
        {
            return groupName[1..];
        }

        return "1.0";
    }
}
