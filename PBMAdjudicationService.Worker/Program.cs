using PBMAdjudication.Worker;
using PBMAdjudication.Core;
using PBMAdjudication.Core.Codec;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;
using Temporalio.Common;
using Temporalio.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddHttpClient();

// ── Codec / DataConverter setup ──────────────────────────────────────────────
var enableEncryption    = builder.Configuration.GetValue<bool>("Temporal:EnableEncryption");
var enableClaimCheck    = builder.Configuration.GetValue<bool>("Temporal:EnableClaimCheck");
var keyBase64           = CodecKeyHelper.GetKeyFromConfig(builder.Configuration);
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
    ? $"[Codec] Claim Check ENABLED — store: {claimCheckStorePath}"
    : "[Codec] Claim Check DISABLED");
// ─────────────────────────────────────────────────────────────────────────────

// ── Worker Versioning setup ───────────────────────────────────────────────────
var buildId        = builder.Configuration["BUILD_ID"] ?? "1.0";
var deploymentName = builder.Configuration["DEPLOYMENT_NAME"] ?? "pbm-adjudication";
var useVersioning  = builder.Configuration.GetValue<bool>("USE_VERSIONING");
var useGlp1Split   = builder.Configuration.GetValue<bool>("USE_GLP1_SPLIT");

// Make the flag available to workflow code via a static — workflows are
// instantiated by the Temporal worker and can't receive DI constructor args.
PrescriptionWorkflow.UseGlp1Split = useGlp1Split;

Console.WriteLine(useVersioning
    ? $"[Versioning] ENABLED — deployment: {deploymentName}, build: {buildId}"
    : "[Versioning] DISABLED — running unversioned worker");
Console.WriteLine(useGlp1Split
    ? "[GLP-1] Split-track adjudication ENABLED (v2 path)"
    : "[GLP-1] Split-track adjudication DISABLED (v1 path — standard single-track)");
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
    var client            = sp.GetRequiredService<ITemporalClient>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var configuration     = sp.GetRequiredService<IConfiguration>();
    var activities        = new PrescriptionActivities(httpClientFactory, configuration);

    var workerOptions = new TemporalWorkerOptions("prescription-task-queue");

    if (useVersioning)
    {
        workerOptions.DeploymentOptions = new WorkerDeploymentOptions(
            new WorkerDeploymentVersion(deploymentName, buildId),
            useWorkerVersioning: true)
        {
            // Pinned: each execution stays on the version it started on.
            // This is the key property that lets v1 GLP-1 claims (still awaiting
            // specialty auth) continue running safely while v2 handles new claims.
            DefaultVersioningBehavior = VersioningBehavior.Pinned
        };
    }

    workerOptions.AddWorkflow<PrescriptionWorkflow>();
    workerOptions.AddWorkflow<StandardAdjudicationWorkflow>();
    workerOptions.AddWorkflow<Glp1AdjudicationWorkflow>();
    workerOptions.AddAllActivities(activities);

    return new TemporalWorkerService(client, workerOptions);
});

await builder.Build().RunAsync();
