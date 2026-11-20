using System.Text.Json.Serialization;
using Intellifer.Hub.Api;
using Intellifer.Hub.Application;
using Intellifer.Hub.Infrastructure;
using Intellifer.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Docker health check: "dotnet Intellifer.Hub.Api.dll --healthcheck" asks the running API whether it is ready.
// It replaces curl, so the image needs no extra packages.
if (args.Contains("--healthcheck"))
{
    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var response = await probe.GetAsync("http://localhost:8080/health/ready");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);
builder.Host.UseSerilog((context, configuration) => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console());

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHubApplication(builder.Configuration);
builder.Services.AddHubInfrastructure(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Hub")
    ?? throw new InvalidOperationException("ConnectionStrings:Hub is required.");
builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "hub-db");

var app = builder.Build();
app.UseSerilogRequestLogging();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
        correlationId = Guid.NewGuid().ToString("n");

    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        await next();
});

// The projects page (wwwroot/admin). Its data calls go to /api/... with the hub admin key.
// Exact "/admin" only: routing treats "/admin" and "/admin/" as the same path, so a mapped route would loop.
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value == "/admin")
    {
        context.Response.Redirect("/admin/");
        return;
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");
app.MapHubEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<HubDbContext>();
    await database.Database.MigrateAsync();
}

await app.RunAsync();
return 0;
