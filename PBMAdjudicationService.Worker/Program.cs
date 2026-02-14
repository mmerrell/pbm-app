using PBMAdjudicationService;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ITemporalClient>(sp =>
{
    return TemporalClient.ConnectAsync(new("localhost:7233")).Result;
});

builder.Services.AddHostedService(sp =>
{
    var client = sp.GetRequiredService<ITemporalClient>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var configuration = sp.GetRequiredService<IConfiguration>();
    var activities = new PrescriptionActivities(httpClientFactory, configuration);
    return new TemporalWorkerService(
        client,
        new TemporalWorkerServiceOptions("prescription-task-queue")
            .AddWorkflow<PrescriptionWorkflow>()
            .AddAllActivities(activities)
    );
});

await builder.Build().RunAsync();
