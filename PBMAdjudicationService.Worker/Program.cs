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
builder.Services.AddHttpClient();

// ── Codec / DataConverter setup ──────────────────────────────────────────────
var enableEncryption = builder.Configuration.GetValue<bool>("Temporal:EnableEncryption");

DataConverter dataConverter;
if (enableEncryption)
{
    var keyBase64 = CodecKeyHelper.GetKeyFromConfig(builder.Configuration);
    dataConverter = DataConverter.Default with { PayloadCodec = new EncryptionCodec(keyBase64) };
    Console.WriteLine("[Codec] Payload encryption ENABLED — PII will be opaque in Temporal UI");
}
else
{
    dataConverter = DataConverter.Default;
    Console.WriteLine("[Codec] Payload encryption DISABLED — data visible in Temporal UI");
}
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<ITemporalClient>(sp =>
{
    return TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
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
