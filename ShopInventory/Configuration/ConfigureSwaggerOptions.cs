using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ShopInventory.Configuration;

public sealed class ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider) : IConfigureOptions<SwaggerGenOptions>
{
    public void Configure(SwaggerGenOptions options)
    {
        foreach (var description in provider.ApiVersionDescriptions)
        {
            options.SwaggerDoc(description.GroupName, CreateInfo(description));
        }

        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter your JWT token"
        });

        options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Name = "X-API-Key",
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Description = "Enter your API Key"
        });

        // 2.x replaced the "empty scheme carrying an OpenApiReference" idiom with a first-class
        // reference type, and the requirement is now built per document so each reference can be
        // resolved against the document that hosts its security definitions. The rendered output is the
        // same $ref with the same empty scope list as before.
        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            { new OpenApiSecuritySchemeReference("Bearer", document), new List<string>() },
            { new OpenApiSecuritySchemeReference("ApiKey", document), new List<string>() }
        });

        options.OperationFilter<ApiVersionOperationFilter>();
        options.EnableAnnotations();
    }

    private static OpenApiInfo CreateInfo(ApiVersionDescription description)
    {
        var info = new OpenApiInfo
        {
            Title = "Shop Inventory API",
            Version = description.ApiVersion.ToString(),
            Description = "A comprehensive inventory management API with SAP Business One integration for retail operations in Zimbabwe.",
            Contact = new OpenApiContact
            {
                Name = "Shop Inventory Support",
                Email = "support@shopinventory.co.zw"
            },
            License = new OpenApiLicense
            {
                Name = "Proprietary License"
            }
        };

        if (description.IsDeprecated)
        {
            info.Description += " This API version has been deprecated.";
        }

        return info;
    }
}