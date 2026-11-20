using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Intellifer.Hub.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddHubApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HubOptions>(configuration.GetSection(HubOptions.Section));
        services.Configure<OpenWaOptions>(configuration.GetSection(OpenWaOptions.Section));
        services.Configure<GmailOptions>(configuration.GetSection(GmailOptions.Section));
        services.Configure<OdooOptions>(configuration.GetSection(OdooOptions.Section));
        services.AddSingleton<ISenderNormalizer, SenderNormalizer>();
        services.AddSingleton<CustomerMatcher>();
        services.Configure<PlaneOptions>(configuration.GetSection(PlaneOptions.Section));
        services.AddSingleton<IssueClassifier>();
        services.AddSingleton(TimeProvider.System);
        // Singleton so the Plane project list is cached between worker runs.
        services.AddSingleton<PlaneProjectResolver>();
        services.AddScoped<PlaneIntakeDispatcher>();
        services.AddScoped<PlaneStatusWatcher>();
        services.AddScoped<OutboundDispatcher>();
        services.AddScoped<AttachmentDispatcher>();
        services.AddScoped<TeamNotifier>();
        services.AddScoped<AppErrorIntake>();
        services.AddScoped<AppErrorDispatcher>();
        services.AddSingleton<OpenWaEventInterpreter>();
        services.AddScoped<IInboundIntake, InboundIntake>();
        services.AddScoped<GmailInboundSync>();
        return services;
    }
}
