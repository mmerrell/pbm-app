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
                    encCodec.SetEnabled(update.EnableEncryption);
                    ccCodec.SetEnabled(update.EnableClaimCheck);
                    return Results.Ok(new { saved = true, restartRequired = false });
                });

            // ── Network proxy passthrough ────────────────────────────────────────────
            // Forwards /api/proxy/* to the network proxy's control API so the frontend
            // doesn't need to make cross-origin requests to a different port.
            //
            // GET  /api/proxy/endpoints        → proxy :5001/endpoints
            // GET  /api/proxy/rules            → proxy :5001/rules
            // POST /api/proxy/rules/{endpoint} → proxy :5001/rules/{endpoint}
            // POST /api/proxy/reset            → proxy :5001/reset

            var proxyControlUrl = builder.Configuration["NetworkProxy:ControlUrl"] ?? "http://proxy:5001";

            app.MapGet("/api/proxy/{**path}", async (string path, IHttpClientFactory factory) =>
            {
                var client = factory.CreateClient();
                var resp = await client.GetAsync($"{proxyControlUrl}/{path}");
                var body = await resp.Content.ReadAsStringAsync();
                return Results.Text(body, "application/json", System.Text.Encoding.UTF8, (int)resp.StatusCode);
            });

            app.MapPost("/api/proxy/{**path}", async (string path, HttpRequest request, IHttpClientFactory factory) =>
            {
                var client = factory.CreateClient();
                var body = await new StreamReader(request.Body).ReadToEndAsync();
                var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await client.PostAsync($"{proxyControlUrl}/{path}", content);
                var respBody = await resp.Content.ReadAsStringAsync();
                return Results.Text(respBody, "application/json", System.Text.Encoding.UTF8, (int)resp.StatusCode);
            });

            // STEP 1: Validate Eligibility
            app.MapPost("/api/validate/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Validating";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Checking eligibility for {prescription.PatientName}");

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
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Authorizing";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Checking prior authorization for {prescription.Medication}");

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
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Adjudicating";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [adjudicate] Calculating copay for {prescription.Medication}");

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

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📋 [approval] Doctor approval requested for {prescription.PatientName}");

                return Results.Ok(new { approvalNeeded = true, approvalId = approvalRequest.Id });
            });

            // Send reminder to doctor
            app.MapPost("/api/remind-doctor/{approvalId}", async (
                string approvalId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var approval = await repo.GetApprovalRequestAsync(approvalId);
                if (approval is null) return Results.NotFound();

                approval.ReminderCount++;
                await repo.UpsertApprovalRequestAsync(approval);

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
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.NotificationStatus = "Sent";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📧 [notify] Sent to {recipient} ({recipientName}): {message}");

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
                // Clear retry banner — replaced by the permanent failure banner
                prescription.ActivityRetryStatus = null; // cleared on notify-failed
                prescription.ActivityRetryStep   = null;
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                return Results.Ok();
            });

            // ── Activity retry state ─────────────────────────────────────────────────
            // Called by the worker's PostAsync helper when an activity HTTP call fails
            // (before Temporal retries it) and when it succeeds (to clear the state).
            // Drives the cyan pulsing pip on the prescription timeline in the UI.

            app.MapPost("/api/activity-retrying/{prescriptionId}/{step}", async (
                string prescriptionId,
                string step,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.ActivityRetryStatus = "Retrying";
                prescription.ActivityRetryStep   = step;
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🔄 [retry] Activity '{step}' failed for {prescription.PatientName} — Temporal will retry");

                return Results.Ok();
            });

            app.MapPost("/api/activity-retry-cleared/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                if (prescription.ActivityRetryStatus == "Retrying")
                {
                    prescription.ActivityRetryStatus = null;
                    prescription.ActivityRetryStep   = null;
                    await repo.UpsertPrescriptionAsync(prescription);
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                }

                return Results.Ok();
            });

            // STEP 5: Submit to Pharmacy
            app.MapPost("/api/submit/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "Submitting";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [submit] Submitting to pharmacy for {prescription.PatientName}");

                await Task.Delay(100);

                prescription.Status = "Completed";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [submit] Prescription completed for {prescription.PatientName}");

                return Results.Ok(new { submitted = true });
            });

            // GLP-1 SPECIALTY ENDPOINTS (v2+)
            // ============================================================================

            // STEP 3a: Adjudicate GLP-1 claim through specialty endpoint
            app.MapPost("/api/adjudicate-glp1/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "AdjudicatingGlp1";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"💊 [adjudicate-glp1] Routing {prescription.Medication} to specialty adjudication endpoint");

                await Task.Delay(150);

                prescription.Copay = Random.Shared.Next(50, 200);
                prescription.Status = "Adjudicated";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"💊 [adjudicate-glp1] Specialty copay calculated: ${prescription.Copay:F2} for {prescription.PatientName}");

                return Results.Ok(new { copay = prescription.Copay });
            });

            // STEP 3b: Request GLP-1 specialty prior authorization (always required)
            app.MapPost("/api/request-specialty-auth/{prescriptionId}", async (
                string prescriptionId,
                string patientName,
                string medication,
                IPrescriptionRepository repo,
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
                    $"🔬 [specialty-auth] GLP-1 specialty prior authorization requested for {patientName} — {medication}");
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🔬 [specialty-auth] Regulatory requirement: CMS mandate effective Q1 2025 — all GLP-1 claims require clinical review");

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

                var glp1WorkflowId = $"{specialtyRequest.PrescriptionId}-glp1";
                var handle = client.GetWorkflowHandle(glp1WorkflowId);

                if (approved)
                {
                    await handle.SignalAsync((Glp1AdjudicationWorkflow wf) => wf.SpecialtyApproveAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [temporal] Clinical reviewer approved GLP-1 specialty auth for {prescription.PatientName}");
                    specialtyRequest.IsApproved = true;
                    prescription.Status = "SpecialtyAuthApproved";
                }
                else
                {
                    await handle.SignalAsync((Glp1AdjudicationWorkflow wf) => wf.SpecialtyDenyAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"❌ [temporal] Clinical reviewer denied GLP-1 specialty auth for {prescription.PatientName}");
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
                    $"⏰ [specialty-auth] GLP-1 specialty authorization timed out for {prescription.PatientName} — no clinical reviewer response");

                return Results.Ok();
            });

            // STEP 3c: Submit GLP-1 line to specialty pharmacy
            app.MapPost("/api/submit-specialty/{prescriptionId}", async (
                string prescriptionId,
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                prescription.Status = "SubmittingSpecialty";
                await repo.UpsertPrescriptionAsync(prescription);
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"💊 [submit-specialty] Submitting GLP-1 line to specialty pharmacy for {prescription.PatientName}");

                await Task.Delay(100);

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [submit-specialty] GLP-1 specialty pharmacy submission complete for {prescription.PatientName}");

                return Results.Ok(new { submitted = true });
            });

            // Generate load
            app.MapPost("/api/admin/generate-load", async (
                IPrescriptionRepository repo,
                IHubContext<NotificationHub> hubContext) =>
            {
                var medications = new[] { "Lipitor", "Metformin", "Lisinopril", "Atorvastatin", "Omeprazole", "Amlodipine", "Simvastatin" };
                var glp1Medications = new[] { "Ozempic 0.5mg (semaglutide)", "Wegovy 2.4mg (semaglutide)", "Mounjaro 5mg (tirzepatide)", "Zepbound 5mg (tirzepatide)" };
                var firstNames = new[] { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Barbara" };
                var lastNames = new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez" };

                for (int i = 0; i < 20; i++)
                {
                    DateTime eligibleDate;
                    if (i < 2)
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(1, 3));
                    else
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(-120, 0));

                    var isGlp1 = (i % 5 == 4);
                    var medication = isGlp1
                        ? glp1Medications[Random.Shared.Next(glp1Medications.Length)]
                        : $"{medications[Random.Shared.Next(medications.Length)]} {Random.Shared.Next(10, 80)}mg";

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

                await hubContext.Clients.All.SendAsync("ReceiveLog", $"📦 [admin] Generated 20 test prescriptions (4 GLP-1)");
                return Results.Ok(new { generated = 20 });
            });

            // Reset
            app.MapPost("/api/admin/reset", async (
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                await foreach (var wf in client.ListWorkflowsAsync("ExecutionStatus = 'Running'"))
                {
                    try { await client.GetWorkflowHandle(wf.Id).TerminateAsync("Reset All triggered"); }
                    catch { /* best-effort */ }
                }

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
                bool isGlp1,
                IPrescriptionRepository repo,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                var prescription = await repo.GetPrescriptionAsync(prescriptionId);
                if (prescription is null) return Results.NotFound();

                var input = new PrescriptionInput
                {
                    PrescriptionId = prescriptionId,
                    PatientName    = prescription.PatientName,
                    Medication     = prescription.Medication,
                    EligibleDate   = prescription.EligibleDate,
                    RefillsRemaining = prescription.RefillsRemaining,
                    IsGlp1         = isGlp1
                };

                if (isGlp1)
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"💊 [temporal] GLP-1 detected — will use split-track adjudication on v2 workers");

                var workflowId = $"prescription-{prescription.PatientId}-{prescriptionId}";
                try
                {
                    await client.StartWorkflowAsync(
                        (PrescriptionWorkflow wf) => wf.RunAsync(input),
                        new WorkflowOptions
                        {
                            Id = workflowId,
                            TaskQueue = "prescription-task-queue"
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
                    PrescriptionId   = prescriptionId,
                    PatientName      = prescription.PatientName,
                    Medication       = prescription.Medication,
                    EligibleDate     = prescription.EligibleDate,
                    RefillsRemaining = prescription.RefillsRemaining,
                    IsGlp1           = isGlp1
                };

                if (isGlp1)
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"💊 [temporal] GLP-1 detected — will use split-track adjudication on v2 workers");

                var sizeKb = (request.ImageData?.Length ?? 0) * 3 / 4 / 1024;
                var claimCheckNote = ccCodec.IsEnabled
                    ? $"🗄️ [claim-check] Image (~{sizeKb} KB) will be offloaded to external storage"
                    : $"⚠️ [claim-check] Claim Check DISABLED — {sizeKb} KB image will be sent raw to Temporal";

                await hubContext.Clients.All.SendAsync("ReceiveLog", claimCheckNote);

                var workflowId = $"prescription-{prescription.PatientId}-{prescriptionId}";
                try
                {
                    await client.StartWorkflowAsync(
                        (PrescriptionWorkflow wf) => wf.RunAsync(input, request.ImageData),
                        new WorkflowOptions
                        {
                            Id = workflowId,
                            TaskQueue = "prescription-task-queue"
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

public record FeatureFlagsUpdate(bool EnableEncryption, bool EnableClaimCheck);
public record WorkflowStartWithImageRequest(string? ImageData);
