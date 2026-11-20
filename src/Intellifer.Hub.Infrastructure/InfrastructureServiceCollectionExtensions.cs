using Intellifer.Hub.Application;
using Intellifer.Hub.Infrastructure.Gmail;
using Intellifer.Hub.Infrastructure.Odoo;
using Intellifer.Hub.Infrastructure.Plane;
using Intellifer.Hub.Infrastructure.Replies;
using Intellifer.Hub.Infrastructure.OpenWa;
using Intellifer.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Intellifer.Hub.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddHubInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Hub");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Hub is required.");

        services.AddDbContext<HubDbContext>(options => HubDbContext.Configure(options, connectionString));
        services.AddScoped<IInboundStore, InboundStore>();
        services.AddScoped<IGmailSyncStateStore, GmailSyncStateStore>();
        services.AddScoped<IChannelHealthStore, ChannelHealthStore>();
        services.AddScoped<IInboundQuery, InboundQuery>();
        services.AddSingleton<GmailMailboxClient>();
        services.AddSingleton<IGmailMailbox>(provider => provider.GetRequiredService<GmailMailboxClient>());
        services.AddScoped<IPlaneStatusStore, PlaneStatusStore>();
        services.AddScoped<IOutboundQueue, OutboundQueue>();
        services.AddScoped<IAttachmentStore, AttachmentStore>();
        services.AddScoped<ITeamNotificationStore, TeamNotificationStore>();
        services.AddScoped<IAppRegistry, AppRegistry>();
        services.AddScoped<IAppErrorStore, AppErrorStore>();
        // Who works on each project: the hub's project page for now; the Odoo employee API later.
        services.AddScoped<ProjectStore>();
        services.AddScoped<IProjectAdmin>(provider => provider.GetRequiredService<ProjectStore>());
        services.AddScoped<IProjectTeamDirectory>(provider => provider.GetRequiredService<ProjectStore>());
        services.AddSingleton<IReplySender, GmailReplySender>();

        // WhatsApp gateway (OpenWA by rmyndharis). Singleton: it remembers the session id it looked up.
        services.AddHttpClient(OpenWaGateway.HttpClientName);
        services.AddSingleton<OpenWaGateway>();
        services.AddSingleton<IWhatsAppGateway>(provider => provider.GetRequiredService<OpenWaGateway>());
        services.AddSingleton<IOpenWaSessionProbe>(provider => provider.GetRequiredService<OpenWaGateway>());
        services.AddSingleton<IReplySender, OpenWaReplySender>();
        services.AddSingleton<IWhatsAppMedia, OpenWaMediaClient>();
        services.AddScoped<IPlaneQueue, PlaneQueue>();
        services.AddHttpClient<IPlaneGateway, PlaneClient>();
        services.AddHttpClient(OdooCustomerDirectory.HttpClientName);
        // Singleton so the Odoo login and field list are fetched once, not per message.
        services.AddSingleton<ICustomerDirectory, OdooCustomerDirectory>();
        return services;
    }
}
