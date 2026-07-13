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
            // Two independently toggleable codecs stacked in a CompositePayloadCodec.
            //
            // Encode order:  ClaimCheck → Encryption
            //   Large payloads are offloaded first; the resulting small token is
            //   then encrypted so even the reference is opaque in Temporal history.
            //
            // Decode order:  Encryption → ClaimCheck  (CompositePayloadCodec reverses)
            //   Encryption is stripped first to reveal the token, then the token is
            //   resolved back to the original bytes.
            //
            // Both codecs are singletons so the feature-flag endpoint can flip them
            // live on the same instances the TemporalClient is using — no restart needed.

            var initiallyEncrypted  = builder.Configuration.GetValue<bool>("Temporal:EnableEncryption");
            var initiallyClaimCheck = builder.Configuration.GetValue<bool>("Temporal:EnableClaimCheck");
            var keyBase64           = CodecKeyHelper.GetKeyFromConfig(builder.Configuration);
            var claimCheckStorePath = builder.Configuration["Temporal:ClaimCheckStorePath"] ?? "/tmp/claim-check";

            var dynamicEncryptionCodec = new DynamicEncryptionCodec(keyBase64, initiallyEncrypted);
            var dynamicClaimCheckCodec = new DynamicClaimCheckCodec(
                new FileSystemClaimCheckStore(claimCheckStorePath), initiallyClaimCheck);

            builder.Services.AddSingleton(dynamicEncryptionCodec);
            builder.Services.AddSingleton(dynamicClaimCheckCodec);

            var compositeCodec = new CompositePayloadCodec(dynamicClaimCheckCodec, dynamicEncryptionCodec);
            var dataConverter  = DataConverter.Default with { PayloadCodec = compositeCodec };
            // ────────────────────────────────────────────────────────────────────

            var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
            builder.Services.AddSingleton<ITemporalClient>(sp =>
            {
                return TemporalClient.ConnectAsync(new TemporalClientConnectOptions(temporalHost)
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

            app.MapGet("/api/specialty-approvals", async (IPrescriptionRepository repo) =>
            {
                var approvals = await repo.GetPendingSpecialtyApprovalRequestsAsync();
                return Results.Ok(approvals);
            });

            // Feature flags
            app.MapGet("/api/config/features",
                (DynamicEncryptionCodec encCodec, DynamicClaimCheckCodec ccCodec) =>
                {
                    return Results.Ok(new
                    {
                        enableEncryption = encCodec.IsEnabled,
                        enableClaimCheck = ccCodec.IsEnabled
                    });
                });

            app.MapPost("/api/config/features",
                (FeatureFlagsUpdate update, DynamicEncryptionCodec encCodec, DynamicClaimCheckCodec ccCodec) =>
                {
                    // Hot-swap: flip flags on the live singleton codec instances.
                    // No restart required — new workflows immediately use the new settings.
                    encCodec.SetEnabled(update.EnableEncryption);
                    ccCodec.SetEnabled(update.EnableClaimCheck);
                    return Results.Ok(new { saved = true, restartRequired = false });
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
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Checking account verification for {prescription.PatientName}");

                await endpointHelper.SimulateEndpointBehavior("validate");

                if (DateTime.UtcNow < prescription.EligibleDate)
                {
                    var waitTime = prescription.EligibleDate - DateTime.UtcNow;
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"⏰ [validate] Funds not available until {prescription.EligibleDate:yyyy-MM-dd HH:mm:ss} (waiting {waitTime.TotalMinutes:F1} minutes)");

                    prescription.Status = "Waiting";
                    await repo.UpsertPrescriptionAsync(prescription);
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                    return Results.Ok(new { eligible = false, waitUntil = prescription.EligibleDate });
                }

                prescription.Status = "Validated";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Account verified for {prescription.PatientName}");

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
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Running AML/sanctions screening for {prescription.Medication}");

                await endpointHelper.SimulateEndpointBehavior("authorize");
                await Task.Delay(100);

                prescription.Status = "Authorized";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Screening cleared for {prescription.Medication}");

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
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [adjudicate] Calculating FX rate & fees for {prescription.Medication}");

                await endpointHelper.SimulateEndpointBehavior("adjudicate");
                await Task.Delay(100);

                prescription.Copay = Random.Shared.Next(5, 50);
                prescription.Status = "Adjudicated";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [adjudicate] Fee calculated: ${prescription.Copay:F2} for {prescription.PatientName}");

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
                        $"✅ [approval] Prior clean transfers on file ({prescription.RefillsRemaining}), no review needed");
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
                    $"Your transfer request for {prescription.Medication} is waiting for compliance review.");
                await endpointHelper.SendNotification("doctor", "A. Chen, Compliance",
                    $"Please review transfer for {prescription.PatientName}: {prescription.Medication}");

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📋 [approval] Compliance review requested for {prescription.PatientName}");

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
                await endpointHelper.SendNotification("doctor", "A. Chen, Compliance",
                    $"REMINDER ({approval.ReminderCount}): Please review transfer for {approval.PatientName}: {approval.Medication}");

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🔔 [approval] Reminder #{approval.ReminderCount} sent to compliance for {approval.PatientName}");

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

                var workflowId = $"payment-{prescription.PatientId}-{approval.PrescriptionId}";
                var handle = client.GetWorkflowHandle(workflowId);

                if (approved)
                {
                    await handle.SignalAsync((PaymentWorkflow wf) => wf.ApproveAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [temporal] Compliance approved transfer for {prescription.PatientName}");
                    approval.IsApproved = true;
                    prescription.Status = "Approved";
                    prescription.RefillsRemaining = 3;
                }
                else
                {
                    await handle.SignalAsync((PaymentWorkflow wf) => wf.DenyAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"❌ [temporal] Compliance rejected transfer for {prescription.PatientName}");
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
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [submit] Submitting to payment rail for {prescription.PatientName}");

                await endpointHelper.SimulateEndpointBehavior("submit");
                await Task.Delay(100);

                prescription.Status = "Completed";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                await endpointHelper.SendNotification("patient", prescription.PatientName,
                    $"Your payment for {prescription.Medication} has been submitted for settlement. Fee: ${prescription.Copay:F2}");

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [submit] Payment completed for {prescription.PatientName}");

                return Results.Ok(new { submitted = true });
            });

            // GLP-1 SPECIALTY ENDPOINTS (v2+)
            // ============================================================================

            // STEP 3a: Adjudicate GLP-1 claim through specialty endpoint
            app.MapPost("/api/adjudicate-glp1/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "AdjudicatingGlp1";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"💊 [adjudicate-glp1] Routing {prescription.Medication} to EDD adjudication endpoint");

                await endpointHelper.SimulateEndpointBehavior("adjudicate-glp1");
                await Task.Delay(150);

                prescription.Copay = Random.Shared.Next(50, 200); // GLP-1s carry higher copay
                prescription.Status = "Adjudicated";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"💊 [adjudicate-glp1] EDD fee calculated: ${prescription.Copay:F2} for {prescription.PatientName}");

                return Results.Ok(new { copay = prescription.Copay });
            });

            // STEP 3b: Request GLP-1 specialty prior authorization (always required)
            app.MapPost("/api/request-specialty-auth/{prescriptionId}", async (
                string prescriptionId,
                string patientName,
                string medication,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "SpecialtyAuthPending";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                var specialtyRequest = new SpecialtyApprovalRequest
                {
                    PrescriptionId = prescriptionId,
                    PatientName = patientName,
                    Medication = medication
                };
                await repo.UpsertSpecialtyApprovalRequestAsync(specialtyRequest);
                await hubContext.Clients.All.SendAsync("SpecialtyApprovalRequestUpdated", specialtyRequest);

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🔬 [specialty-auth] Enhanced due diligence review requested for {patientName} — {medication}");
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🔬 [specialty-auth] Regulatory requirement: FinCEN mandate effective Q1 2025 — all high-risk transfers require enhanced due diligence review");

                return Results.Ok(new { specialtyAuthId = specialtyRequest.Id });
            });

            // Clinical reviewer approves/denies GLP-1 specialty authorization
            app.MapPost("/api/specialty-approve/{specialtyAuthId}", async (
                string specialtyAuthId,
                bool approved,
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                var specialtyRequest = await repo.GetSpecialtyApprovalRequestAsync(specialtyAuthId);
                if (specialtyRequest is null) return Results.NotFound();

                var prescription = await repo.GetPrescriptionAsync(specialtyRequest.PrescriptionId);
                if (prescription is null) return Results.NotFound();

                // Signal the GLP-1 child workflow specifically — note the child workflow ID
                var glp1WorkflowId = $"{specialtyRequest.PrescriptionId}-glp1";
                var handle = client.GetWorkflowHandle(glp1WorkflowId);

                if (approved)
                {
                    await handle.SignalAsync((EddAdjudicationWorkflow wf) => wf.SpecialtyApproveAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [temporal] Compliance reviewer cleared EDD review for {prescription.PatientName}");
                    specialtyRequest.IsApproved = true;
                    prescription.Status = "SpecialtyAuthApproved";
                }
                else
                {
                    await handle.SignalAsync((EddAdjudicationWorkflow wf) => wf.SpecialtyDenyAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"❌ [temporal] Compliance reviewer rejected EDD review for {prescription.PatientName}");
                    specialtyRequest.IsDenied = true;
                    prescription.Status = "Denied";
                }

                await repo.UpsertPrescriptionAsync(prescription);
                await repo.UpsertSpecialtyApprovalRequestAsync(specialtyRequest);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("SpecialtyApprovalRequestUpdated", specialtyRequest);

                return Results.Ok();
            });

            // GLP-1 specialty auth timeout handler
            app.MapPost("/api/specialty-auth-timeout/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                var specialtyRequest = await repo.GetSpecialtyApprovalRequestByPrescriptionAsync(prescriptionId);
                if (specialtyRequest is not null)
                {
                    specialtyRequest.IsTimedOut = true;
                    await repo.UpsertSpecialtyApprovalRequestAsync(specialtyRequest);
                    await hubContext.Clients.All.SendAsync("SpecialtyApprovalRequestUpdated", specialtyRequest);
                }

                prescription.Status = "SpecialtyAuthTimeout";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"⏰ [specialty-auth] EDD review timed out for {prescription.PatientName} — no compliance reviewer response");

                return Results.Ok();
            });

            // STEP 3c: Submit GLP-1 line to specialty pharmacy
            app.MapPost("/api/submit-specialty/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                EndpointHelper endpointHelper,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "SubmittingSpecialty";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"💊 [submit-specialty] Submitting high-risk transfer to enhanced settlement rail for {prescription.PatientName}");

                await endpointHelper.SimulateEndpointBehavior("submit-specialty");
                await Task.Delay(100);

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [submit-specialty] Enhanced settlement rail submission complete for {prescription.PatientName}");

                return Results.Ok(new { submitted = true });
            });

            // Generate load
            app.MapPost("/api/admin/generate-load", async (
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var medications = new[] { "USD → EUR", "USD → GBP", "USD → SGD", "USD → CAD", "USD → AUD", "EUR → JPY", "USD → HKD" };
                var glp1Medications = new[] { "USD → NZD", "EUR → CHF", "USD → INR", "USD → BRL" };
                var firstNames = new[] { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Barbara" };
                var lastNames = new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez" };

                for (int i = 0; i < 20; i++)
                {
                    DateTime eligibleDate;
                    if (i < 2)
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(1, 3));
                    else
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(-120, 0));

                    // Every 5th prescription is a GLP-1 (indices 4, 9, 14, 19)
                    var isGlp1 = (i % 5 == 4);
                    var medication = isGlp1
                        ? glp1Medications[Random.Shared.Next(glp1Medications.Length)]
                        : medications[Random.Shared.Next(medications.Length)];

                    var prescription = new Prescription
                    {
                        PatientId = $"P{100 + i}",
                        PatientName = $"{firstNames[Random.Shared.Next(firstNames.Length)]} {lastNames[Random.Shared.Next(lastNames.Length)]}",
                        Medication = medication,
                        EligibleDate = eligibleDate,
                        RefillsRemaining = Random.Shared.Next(0, 4),
                        Status = "Pending"
                    };

                    await repo.UpsertPrescriptionAsync(prescription);
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                }

                await hubContext.Clients.All.SendAsync("ReceiveLog", $"📦 [admin] Generated 20 test payments (4 high-risk/EDD)");
                return Results.Ok(new { generated = 20 });
            });

            // Reset
            app.MapPost("/api/admin/reset", async (
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                // Terminate all running workflows before resetting DB
                await foreach (var wf in client.ListWorkflowsAsync("ExecutionStatus = 'Running'"))
                {
                    try { await client.GetWorkflowHandle(wf.Id).TerminateAsync("Reset All triggered"); }
                    catch { /* best-effort — ignore if already completed */ }
                }

                await repo.ResetAsync();

                var prescriptions = await repo.GetAllPrescriptionsAsync();
                foreach (var rx in prescriptions)
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", rx);

                await hubContext.Clients.All.SendAsync("ReceiveLog", $"🔄 [admin] System reset - restored 5 default payments");
                return Results.Ok(new { reset = true });
            });

            // Start Temporal workflow
            app.MapPost("/api/workflow/start/{prescriptionId}", async (
                string prescriptionId,
                bool isGlp1,
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                var input = new PrescriptionInput
                {
                    TransferId          = prescriptionId,
                    CustomerName        = prescription.PatientName,
                    CurrencyCorridor    = prescription.Medication,
                    FundsAvailableDate  = prescription.EligibleDate,
                    PriorCleanTransfers = prescription.RefillsRemaining,
                    IsHighRisk          = isGlp1
                };

                if (isGlp1)
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"💊 [temporal] High-risk/EDD transfer detected — will use split-track adjudication on v2 workers");

                var workflowId = $"payment-{prescription.PatientId}-{prescriptionId}";
                try
                {
                    await client.StartWorkflowAsync(
                        (PaymentWorkflow wf) => wf.RunAsync(input),
                        new WorkflowOptions
                        {
                            Id = workflowId,
                            TaskQueue = "payment-task-queue"
                        });
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"🚀 [temporal] Started workflow for {prescription.PatientName}");
                }
                catch (Temporalio.Exceptions.WorkflowAlreadyStartedException)
                {
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"⚡ [temporal] Workflow already running for {prescription.PatientName} — attaching");
                }

                return Results.Ok(new { workflowId });
            });

            // Start Temporal workflow with an attached image (claim check demo path)
            app.MapPost("/api/workflow/start-with-image/{prescriptionId}", async (
                string prescriptionId,
                bool isGlp1,
                WorkflowStartWithImageRequest request,
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                DynamicClaimCheckCodec ccCodec,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                var input = new PrescriptionInput
                {
                    TransferId          = prescriptionId,
                    CustomerName        = prescription.PatientName,
                    CurrencyCorridor    = prescription.Medication,
                    FundsAvailableDate  = prescription.EligibleDate,
                    PriorCleanTransfers = prescription.RefillsRemaining,
                    IsHighRisk          = isGlp1
                    // ImageData intentionally omitted — passed as a separate workflow
                    // argument so the ClaimCheckCodec only offloads the image payload,
                    // leaving the Rx fields visible in Temporal history.
                };

                if (isGlp1)
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"💊 [temporal] High-risk/EDD transfer detected — will use split-track adjudication on v2 workers");

                var sizeKb = (request.ImageData?.Length ?? 0) * 3 / 4 / 1024; // rough base64 → bytes
                var claimCheckNote = ccCodec.IsEnabled
                    ? $"🗄️ [claim-check] Image (~{sizeKb} KB) will be offloaded to external storage"
                    : $"⚠️ [claim-check] Claim Check DISABLED — {sizeKb} KB image will be sent raw to Temporal";

                await hubContext.Clients.All.SendAsync("ReceiveLog", claimCheckNote);

                var workflowId = $"payment-{prescription.PatientId}-{prescriptionId}";
                try
                {
                    await client.StartWorkflowAsync(
                        (PaymentWorkflow wf) => wf.RunAsync(input, request.ImageData),
                        new WorkflowOptions
                        {
                            Id = workflowId,
                            TaskQueue = "payment-task-queue"
                        });
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"🚀 [temporal] Started workflow for {prescription.PatientName}");
                }
                catch (Temporalio.Exceptions.WorkflowAlreadyStartedException)
                {
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"⚡ [temporal] Workflow already running for {prescription.PatientName} — attaching");
                }

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
                    $"📝 [admin] Created new payment for {prescription.PatientName}");
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
                    $"⏰ [approval] Review timeout for {prescription.PatientName} - no response from compliance");

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
                    $"⚠️ [admin] Payment on hold for {prescription.PatientName} - settlement failed, awaiting manual intervention");

                return Results.Ok();
            });

            app.Run();
        }
    }
}

public record FeatureFlagsUpdate(bool EnableEncryption, bool EnableClaimCheck);
public record WorkflowStartWithImageRequest(string? ImageData);
