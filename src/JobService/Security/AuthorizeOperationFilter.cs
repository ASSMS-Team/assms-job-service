using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace JobService.Security;

public sealed class AuthorizeOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var attributes = context.MethodInfo.DeclaringType?.GetCustomAttributes(true)
            .Concat(context.MethodInfo.GetCustomAttributes(true)) ?? Array.Empty<object>();
        if (attributes.OfType<AllowAnonymousAttribute>().Any() || !attributes.OfType<AuthorizeAttribute>().Any()) return;

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
                }] = Array.Empty<string>(),
            },
        ];
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication is missing, invalid or expired." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "The authenticated role is not permitted." });
    }
}
