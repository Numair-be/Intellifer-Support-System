using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Api;

public sealed class ApiKeyFilter(IOptions<HubOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(expected))
            return Results.Json(new { error = "Hub API key is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var provided)
            || !SecretComparer.Equals(provided.ToString(), expected))
            return Results.Unauthorized();

        return await next(context);
    }
}
