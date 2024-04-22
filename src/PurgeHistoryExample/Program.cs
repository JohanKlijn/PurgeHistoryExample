using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var hostBuilder = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        string? connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process);
        services.AddAzureClients(cfg =>
        {
            cfg.AddTableServiceClient(connectionString);
        });
    })
    .ConfigureLogging((hostingContext, logging) =>
    {
        // Make sure the configuration of the appsettings.json file is picked up.
        logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));

        // Add console loggig. In the appsettings.json, we specified we only want warnings and information for CyberTwice.IPP.Validation.
        // More information we do not need we runnig in containers (other information is send to Applicaton Insights).
        logging.AddConsole();

        // The Application Insights SDK adds a default logging filter that instructs ILogger to capture only Warning and more severe logs. Application Insights requires an explicit override.
        // Log levels can also be configured using appsettings.json. For more information, see https://learn.microsoft.com/en-us/azure/azure-monitor/app/worker-service#ilogger-logs
        logging.Services.Configure<LoggerFilterOptions>(options =>
        {
            LoggerFilterRule? defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName
                == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (defaultRule is not null)
            {
                options.Rules.Remove(defaultRule);
            }
        });
    });

var host = hostBuilder.Build();

host.Run();
