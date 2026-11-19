using Intellifer.Hub.Application;
using Intellifer.Hub.Infrastructure;
using Intellifer.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(configuration => configuration
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console());
builder.Services.AddHubApplication(builder.Configuration);
builder.Services.AddHubInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Intellifer.Hub.Worker.GmailPoller>();
builder.Services.AddHostedService<Intellifer.Hub.Worker.PlaneIntakePoller>();
builder.Services.AddHostedService<Intellifer.Hub.Worker.PlaneStatusPoller>();
builder.Services.AddHostedService<Intellifer.Hub.Worker.OutboundPoller>();
builder.Services.AddHostedService<Intellifer.Hub.Worker.AttachmentPoller>();
builder.Services.AddHostedService<Intellifer.Hub.Worker.WhatsAppSetupPoller>();

var host = builder.Build();
await using (var scope = host.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<HubDbContext>();
    await database.Database.MigrateAsync();
}

await host.RunAsync();
