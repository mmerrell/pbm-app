using PBMAdjudication.Worker;
using PBMAdjudication.Core;
using PBMAdjudication.Core.Codec;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddHttpClient();

// ── Codec / DataConverter setup ──────────────────────────────────────────────
var enableEncryption = builder.Configuration.GetValue<bool>("Temporal:EnableEncryption");
var keyBase64 = CodecKeyHelper.GetKeyFromConfig(builder.Configuration);
var dynamicCodec = new DynamicEncryptionCodec(keyBase64, enableEncryption);
var dataConverter = DataConverter.Default with { PayloadCodec = dynamicCodec };

Console.WriteLine(enableEncryption
    ? "[Codec] Payload encryption ENABLED — PII will be opaque in Temporal UI"
    : "[Codec] Payload encryption DISABLED — data visible in Temporal UI");
// ─────────────────────────────────────────────────────────────────────────────

var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
builder.Services.AddSingleton<ITemporalClient>(sp =>
{
    return TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalHost)
    {
        DataConverter = dataConverter
    }).Result;
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
