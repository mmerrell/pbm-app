using PBMAdjudication.Worker;
using PBMAdjudication.Core;
using PBMAdjudication.Core.Codec;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Temporalio.Client;
using Temporalio.Converters;

namespace PBMAdjudicationService
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

            var connectionString = builder.Configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Postgres connection string not found");

            builder.Services.AddHttpClient();

            // ── Codec / DataConverter setup ──────────────────────────────────────
            var enableEncryption = builder.Configuration.GetValue<bool>("Temporal:EnableEncryption");
            DataConverter dataConverter;
            if (enableEncryption)
            {
                var keyBase64 = CodecKeyHelper.GetKeyFromConfig(builder.Configuration);
                dataConverter = DataConverter.Default with { PayloadCodec = new EncryptionCodec(keyBase64) };
            }
            else
            {
                dataConverter = DataConverter.Default;
            }
            // ────────────────────────────────────────────────────────────────────

            builder.Services.AddSingleton<ITemporalClient>(sp =>
            {
                return TemporalClient.ConnectAsync(new TemporalClientConnectOptions("localhost:7233")
                {
                    DataConverter = dataConverter
                }).Result;
            });

            builder.Services.AddSingleton<IPrescriptionRepository>(
                new PostgresPrescriptionRepository(connectionString));

            builder.Services.AddScoped<EndpointHelper>();

            builder.Services.AddSignalR();

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });

            var app = builder.Build();

            // Initialize database
            var initializer = new DatabaseInitializer(connectionString);
            await initializer.InitializeAsync();

            app.UseCors();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapHub<NotificationHub>("/notificationHub");

            // ============================================================================
            // API ENDPOINTS
            // ============================================================================

            app.MapGet("/api/prescriptions", async (IPrescriptionRepository repo) =>
            {
                var prescriptions = await repo.GetAllPrescriptionsAsync();
                return Results.Ok(prescriptions);
            });

            app.MapGet("/api/prescriptions/{id}", async (string id, IPrescriptionRepository repo) =>
            {
                var prescription = await repo.GetPrescriptionAsync(id);
                return prescription is null ? Results.NotFound() : Results.Ok(prescription);
            });

            app.MapGet("/api/approvals", async (IPrescriptionRepository repo) =>
            {
                var approvals = await repo.GetPendingApprovalRequestsAsync();
                return Results.Ok(approvals);
            });

            // Feature flags
            app.MapGet("/api/config/features", (IConfiguration config) =>
            {
                var enableEncryption = config.GetValue<bool>("Temporal:EnableEncryption");
                return Results.Ok(new { enableEncryption });
            });

            app.MapPost("/api/config/features", async (FeatureFlagsUpdate update) =>
            {
                // Update appsettings.json on disk so the change persists across restarts.
                // Note: the running process will NOT pick this up until restarted.
                var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                var json = await File.ReadAllTextAsync(appSettingsPath);
                var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

                if (root["Temporal"] is System.Text.Json.Nodes.JsonObject temporal)
                    temporal["EnableEncryption"] = update.EnableEncryption;
                else
                    root["Temporal"] = new System.Text.Json.Nodes.JsonObject { ["EnableEncryption"] = update.EnableEncryption };

                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(appSettingsPath, root.ToJsonString(options));

                return Results.Ok(new { saved = true, restartRequired = true });
            });

            app.MapGet("/api/config", async (IPrescriptionRepository repo) =>
            {
                var configs = await repo.GetAllEndpointConfigsAsync();
                return Results.Ok(configs);
            });

            app.MapPost("/api/config/{endpoint}", async (string endpoint, EndpointConfig config, IPrescriptionRepository repo) =>
            {
                var existing = await repo.GetEndpointConfigAsync(endpoint);
                if (existing is null) return Results.NotFound();
                await repo.UpsertEndpointConfigAsync(endpoint, config);
                return Results.Ok();
            });

            // STEP 1: Validate Eligibility
            app.MapPost("/api/validate/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Validating";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Checking eligibility for {prescription.PatientName}");

                await endpointHelper.SimulateEndpointBehavior("validate");

                if (DateTime.UtcNow < prescription.EligibleDate)
                {
                    var waitTime = prescription.EligibleDate - DateTime.UtcNow;
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"⏰ [validate] Not eligible until {prescription.EligibleDate:yyyy-MM-dd HH:mm:ss} (waiting {waitTime.TotalMinutes:F1} minutes)");

                    prescription.Status = "Waiting";
                    await repo.UpsertPrescriptionAsync(prescription);
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                    return Results.Ok(new { eligible = false, waitUntil = prescription.EligibleDate });
                }

                prescription.Status = "Validated";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Prescription eligible for {prescription.PatientName}");

                return Results.Ok(new { eligible = true });
            });

            // STEP 2: Check Prior Authorization
            app.MapPost("/api/authorize/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Authorizing";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Checking prior authorization for {prescription.Medication}");

                await endpointHelper.SimulateEndpointBehavior("authorize");
                await Task.Delay(100);

                prescription.Status = "Authorized";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Authorization approved for {prescription.Medication}");

                return Results.Ok(new { authorized = true });
            });

            // STEP 3: Adjudicate Claim
            app.MapPost("/api/adjudicate/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Adjudicating";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [adjudicate] Calculating copay for {prescription.Medication}");

                await endpointHelper.SimulateEndpointBehavior("adjudicate");
                await Task.Delay(100);

                prescription.Copay = Random.Shared.Next(5, 50);
                prescription.Status = "Adjudicated";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [adjudicate] Copay calculated: ${prescription.Copay:F2} for {prescription.PatientName}");

                return Results.Ok(new { copay = prescription.Copay });
            });

            // STEP 4: Request Doctor Approval
            app.MapPost("/api/request-approval/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                if (prescription.RefillsRemaining > 0)
                {
                    prescription.ApprovalNeededReason = null;
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [approval] Refills available ({prescription.RefillsRemaining} remaining), no approval needed");
                    return Results.Ok(new { approvalNeeded = false });
                }

                prescription.ApprovalNeededReason = "NO_REFILLS";
                prescription.Status = "ApprovalNeeded";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                var approvalRequest = new DoctorApprovalRequest
                {
                    PrescriptionId = prescriptionId,
                    PatientName = prescription.PatientName,
                    Medication = prescription.Medication
                };
                await repo.UpsertApprovalRequestAsync(approvalRequest);
                await hubContext.Clients.All.SendAsync("ApprovalRequestUpdated", approvalRequest);

                await endpointHelper.SendNotification("patient", prescription.PatientName,
                    $"Your refill request for {prescription.Medication} is waiting for doctor approval.");
                await endpointHelper.SendNotification("doctor", "Dr. Smith",
                    $"Please approve refill for {prescription.PatientName}: {prescription.Medication}");

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📋 [approval] Doctor approval requested for {prescription.PatientName}");

                return Results.Ok(new { approvalNeeded = true, approvalId = approvalRequest.Id });
            });

            // Send reminder to doctor
            app.MapPost("/api/remind-doctor/{approvalId}", async (
                string approvalId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var approval = await repo.GetApprovalRequestAsync(approvalId);
                if (approval is null) return Results.NotFound();

                approval.ReminderCount++;
                await repo.UpsertApprovalRequestAsync(approval);
                await endpointHelper.SendNotification("doctor", "Dr. Smith",
                    $"REMINDER ({approval.ReminderCount}): Please approve refill for {approval.PatientName}: {approval.Medication}");

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🔔 [approval] Reminder #{approval.ReminderCount} sent to doctor for {approval.PatientName}");

                return Results.Ok();
            });

            // Doctor approves/denies
            app.MapPost("/api/approve/{approvalId}", async (
                string approvalId,
                bool approved,
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                var approval = await repo.GetApprovalRequestAsync(approvalId);
                if (approval is null) return Results.NotFound();

                var prescription = await repo.GetPrescriptionAsync(approval.PrescriptionId);
                if (prescription is null) return Results.NotFound();

                var workflowId = $"prescription-{prescription.PatientId}-{approval.PrescriptionId}";
                var handle = client.GetWorkflowHandle(workflowId);

                if (approved)
                {
                    await handle.SignalAsync((PrescriptionWorkflow wf) => wf.ApproveAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [temporal] Doctor approved workflow for {prescription.PatientName}");
                    approval.IsApproved = true;
                    prescription.Status = "Approved";
                    prescription.RefillsRemaining = 3;
                }
                else
                {
                    await handle.SignalAsync((PrescriptionWorkflow wf) => wf.DenyAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"❌ [temporal] Doctor denied workflow for {prescription.PatientName}");
                    approval.IsDenied = true;
                    prescription.Status = "Denied";
                }

                await repo.UpsertPrescriptionAsync(prescription);
                await repo.UpsertApprovalRequestAsync(approval);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ApprovalRequestUpdated", approval);

                return Results.Ok();
            });

            // Notify
            app.MapPost("/api/notify/{prescriptionId}", async (
                string prescriptionId,
                string recipient,
                string recipientName,
                string message,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.NotificationStatus = "Retrying";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                await endpointHelper.SimulateEndpointBehavior("notify");

                prescription.NotificationStatus = "Sent";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await endpointHelper.SendNotification(recipient, recipientName, message);

                return Results.Ok();
            });

            // Notify failed
            app.MapPost("/api/notify-failed/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.NotificationStatus = "Failed";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                return Results.Ok();
            });

            // STEP 5: Submit to Pharmacy
            app.MapPost("/api/submit/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Submitting";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [submit] Submitting to pharmacy for {prescription.PatientName}");

                await endpointHelper.SimulateEndpointBehavior("submit");
                await Task.Delay(100);

                prescription.Status = "Completed";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                await endpointHelper.SendNotification("patient", prescription.PatientName,
                    $"Your prescription for {prescription.Medication} has been sent to your pharmacy. Copay: ${prescription.Copay:F2}");

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [submit] Prescription completed for {prescription.PatientName}");

                return Results.Ok(new { submitted = true });
            });

            // Generate load
            app.MapPost("/api/admin/generate-load", async (
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var medications = new[] { "Lipitor", "Metformin", "Lisinopril", "Atorvastatin", "Omeprazole", "Amlodipine", "Simvastatin" };
                var firstNames = new[] { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Barbara" };
                var lastNames = new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez" };

                for (int i = 0; i < 20; i++)
                {
                    DateTime eligibleDate;
                    if (i < 2)
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(1, 3));
                    else
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(-120, 0));

                    var prescription = new Prescription
                    {
                        PatientId = $"P{100 + i}",
                        PatientName = $"{firstNames[Random.Shared.Next(firstNames.Length)]} {lastNames[Random.Shared.Next(lastNames.Length)]}",
                        Medication = $"{medications[Random.Shared.Next(medications.Length)]} {Random.Shared.Next(10, 80)}mg",
                        EligibleDate = eligibleDate,
                        RefillsRemaining = Random.Shared.Next(0, 4),
                        Status = "Pending"
                    };

                    await repo.UpsertPrescriptionAsync(prescription);
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                }

                await hubContext.Clients.All.SendAsync("ReceiveLog", $"📦 [admin] Generated 20 test prescriptions");
                return Results.Ok(new { generated = 20 });
            });

            // Reset
            app.MapPost("/api/admin/reset", async (
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                await repo.ResetAsync();

                var prescriptions = await repo.GetAllPrescriptionsAsync();
                foreach (var rx in prescriptions)
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", rx);

                await hubContext.Clients.All.SendAsync("ReceiveLog", $"🔄 [admin] System reset - restored 5 default prescriptions");
                return Results.Ok(new { reset = true });
            });

            // Start Temporal workflow
            app.MapPost("/api/workflow/start/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                var input = new PrescriptionInput
                {
                    PrescriptionId = prescriptionId,
                    PatientName = prescription.PatientName,
                    Medication = prescription.Medication,
                    EligibleDate = prescription.EligibleDate,
                    RefillsRemaining = prescription.RefillsRemaining
                };

                var workflowId = $"prescription-{prescription.PatientId}-{prescriptionId}";
                await client.StartWorkflowAsync(
                    (PrescriptionWorkflow wf) => wf.RunAsync(input),
                    new WorkflowOptions
                    {
                        Id = workflowId,
                        TaskQueue = "prescription-task-queue"
                    });

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🚀 [temporal] Started workflow for {prescription.PatientName}");

                return Results.Ok(new { workflowId });
            });

            // Create prescription
            app.MapPost("/api/prescriptions", async (
                Prescription prescription,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📝 [admin] Created new prescription for {prescription.PatientName}");
                return Results.Ok(prescription);
            });

            // Approval timeout
            app.MapPost("/api/approval-timeout/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                var approval = await repo.GetApprovalRequestByPrescriptionAsync(prescriptionId);
                if (approval != null)
                {
                    approval.IsDenied = true;
                    await repo.UpsertApprovalRequestAsync(approval);
                    await hubContext.Clients.All.SendAsync("ApprovalRequestUpdated", approval);
                }

                prescription.Status = "ApprovalTimeout";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"⏰ [approval] Approval timeout for {prescription.PatientName} - no response from doctor");

                return Results.Ok();
            });

            // On hold
            app.MapPost("/api/on-hold/{prescriptionId}", async (
                string prescriptionId,
                int failedStep,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "OnHold";
                prescription.FailedStep = failedStep;
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"⚠️ [admin] Prescription on hold for {prescription.PatientName} - pharmacy submission failed, awaiting manual intervention");

                return Results.Ok();
            });

            app.Run();
        }
    }
}

public record FeatureFlagsUpdate(bool EnableEncryption);