using PBMAdjudicationService;
using System;
using System.Collections.Generic;
using System.Text;
using Temporalio.Client;
using Temporalio.Extensions.Hosting;

namespace PBMAdjudicationService
{
    public class PBMAdjudicationWorker
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            // Configure Temporal client and worker
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
        }
    }
}
