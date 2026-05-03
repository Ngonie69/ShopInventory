using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ShopInventory.Configuration;

public sealed class ApiVersionOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var apiVersion = GetApiVersion(context);

        operation.Parameters ??= new List<OpenApiParameter>();

        var apiVersionParameter = operation.Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, "api-version", StringComparison.OrdinalIgnoreCase)
            && parameter.In == ParameterLocation.Query);

        if (apiVersionParameter is null)
        {
            apiVersionParameter = new OpenApiParameter
            {
                Name = "api-version",
                In = ParameterLocation.Query,
                Required = false,
                Description = "Optional for version 1.0 requests. Supply this when calling a later API version.",
                Schema = new OpenApiSchema
                {
                    Type = "string"
                }
            };

            operation.Parameters.Add(apiVersionParameter);
        }

        apiVersionParameter.Schema ??= new OpenApiSchema { Type = "string" };
        apiVersionParameter.Schema.Default = new OpenApiString(apiVersion);
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