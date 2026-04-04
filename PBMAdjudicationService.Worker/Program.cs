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
// Must exactly mirror the Api's codec pipeline so the Worker can read history
// written by the Api and vice versa.
//
// Encode order:  ClaimCheck → Encryption
// Decode order:  Encryption → ClaimCheck  (CompositePayloadCodec reverses automatically)

var enableEncryption  = builder.Configuration.GetValue<bool>("Temporal:EnableEncryption");
var enableClaimCheck  = builder.Configuration.GetValue<bool>("Temporal:EnableClaimCheck");
var keyBase64         = CodecKeyHelper.GetKeyFromConfig(builder.Configuration);
var claimCheckStorePath = builder.Configuration["Temporal:ClaimCheckStorePath"] ?? "/tmp/claim-check";

var dynamicEncryptionCodec = new DynamicEncryptionCodec(keyBase64, enableEncryption);
var dynamicClaimCheckCodec = new DynamicClaimCheckCodec(
    new FileSystemClaimCheckStore(claimCheckStorePath), enableClaimCheck);

var compositeCodec = new CompositePayloadCodec(dynamicClaimCheckCodec, dynamicEncryptionCodec);
var dataConverter  = DataConverter.Default with { PayloadCodec = compositeCodec };

Console.WriteLine(enableEncryption
    ? "[Codec] Payload encryption ENABLED"
    : "[Codec] Payload encryption DISABLED");
Console.WriteLine(enableClaimCheck
    ? $"[Codec] Claim Check ENABLED — threshold {dynamicClaimCheckCodec.ThresholdBytes / 1024} KB, store: {claimCheckStorePath}"
    : "[Codec] Claim Check DISABLED — large payloads will hit Temporal size limits");
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
