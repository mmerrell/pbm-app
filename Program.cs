using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Temporalio.Client;

namespace PBMAdjudicationService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            // Add HttpClientFactory for activities
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<ITemporalClient>(sp =>
            {
                return TemporalClient.ConnectAsync(new("localhost:7233")).Result;
            });

            builder.Services.AddSignalR();
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(builder =>
                {
                    builder.AllowAnyOrigin()
                           .AllowAnyMethod()
                           .AllowAnyHeader();
                });
            });

            var app = builder.Build();

            app.UseCors();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapHub<NotificationHub>("/notificationHub");

            // ============================================================================
            // API ENDPOINTS
            // ============================================================================

            app.MapGet("/api/prescriptions", () =>
            {
                return Results.Ok(DataStore.Prescriptions.Values.OrderByDescending(p => p.RequestedDate));
            });

            app.MapGet("/api/prescriptions/{id}", (string id) =>
            {
                if (DataStore.Prescriptions.TryGetValue(id, out var prescription))
                {
                    return Results.Ok(prescription);
                }
                return Results.NotFound();
            });

            app.MapGet("/api/approvals", () =>
            {
                return Results.Ok(DataStore.ApprovalRequests.Values
                    .Where(a => !a.IsApproved && !a.IsDenied)
                    .OrderByDescending(a => a.RequestedAt));
            });

            app.MapGet("/api/config", () =>
            {
                return Results.Ok(DataStore.EndpointConfigs);
            });

            app.MapPost("/api/config/{endpoint}", (string endpoint, EndpointConfig config) =>
            {
                if (DataStore.EndpointConfigs.ContainsKey(endpoint))
                {
                    DataStore.EndpointConfigs[endpoint] = config;
                    return Results.Ok();
                }
                return Results.NotFound();
            });

            // STEP 1: Validate Eligibility
            app.MapPost("/api/validate/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                prescription.Status = "Validating";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Checking eligibility for {prescription.PatientName}");

                await EndpointHelper.SimulateEndpointBehavior("validate", hubContext);

                if (DateTime.UtcNow < prescription.EligibleDate)
                {
                    var waitTime = prescription.EligibleDate - DateTime.UtcNow;
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"⏰ [validate] Not eligible until {prescription.EligibleDate:yyyy-MM-dd HH:mm:ss} (waiting {waitTime.TotalMinutes:F1} minutes)");

                    prescription.Status = "Waiting";
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                    return Results.Ok(new { eligible = false, waitUntil = prescription.EligibleDate });
                }

                prescription.Status = "Validated";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Prescription eligible for {prescription.PatientName}");

                return Results.Ok(new { eligible = true });
            });

            // STEP 2: Check Prior Authorization
            app.MapPost("/api/authorize/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                prescription.Status = "Authorizing";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Checking prior authorization for {prescription.Medication}");

                await EndpointHelper.SimulateEndpointBehavior("authorize", hubContext);
                await Task.Delay(100);

                prescription.Status = "Authorized";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Authorization approved for {prescription.Medication}");

                return Results.Ok(new { authorized = true });
            });

            // STEP 3: Adjudicate Claim
            app.MapPost("/api/adjudicate/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                prescription.Status = "Adjudicating";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [adjudicate] Calculating copay for {prescription.Medication}");
                await EndpointHelper.SimulateEndpointBehavior("adjudicate", hubContext);
                await Task.Delay(100);
                prescription.Copay = Random.Shared.Next(5, 50);

                prescription.Status = "Adjudicated";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [adjudicate] Copay calculated: ${prescription.Copay:F2} for {prescription.PatientName}");

                return Results.Ok(new { copay = prescription.Copay });
            });

            app.MapPost("/api/request-approval/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                if (prescription.RefillsRemaining > 0)
                {
                    prescription.ApprovalNeededReason = null;
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [approval] Refills available ({prescription.RefillsRemaining} remaining), no approval needed");
                    return Results.Ok(new { approvalNeeded = false });
                }

                prescription.ApprovalNeededReason = "NO_REFILLS";
                prescription.Status = "ApprovalNeeded";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                var approvalRequest = new DoctorApprovalRequest
                {
                    PrescriptionId = prescriptionId,
                    PatientName = prescription.PatientName,
                    Medication = prescription.Medication
                };
                DataStore.ApprovalRequests[approvalRequest.Id] = approvalRequest;
                await hubContext.Clients.All.SendAsync("ApprovalRequestUpdated", approvalRequest);
                await EndpointHelper.SendNotification("patient", prescription.PatientName,
                    $"Your refill request for {prescription.Medication} is waiting for doctor approval.", hubContext);
                await EndpointHelper.SendNotification("doctor", "Dr. Smith",
                    $"Please approve refill for {prescription.PatientName}: {prescription.Medication}", hubContext);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📋 [approval] Doctor approval requested for {prescription.PatientName}");
                return Results.Ok(new { approvalNeeded = true, approvalId = approvalRequest.Id });
            });

            // Send reminder to doctor
            app.MapPost("/api/remind-doctor/{approvalId}", async (string approvalId, IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.ApprovalRequests.TryGetValue(approvalId, out var approval))
                {
                    return Results.NotFound();
                }

                approval.ReminderCount++;
                await EndpointHelper.SendNotification("doctor", "Dr. Smith",
                    $"REMINDER ({approval.ReminderCount}): Please approve refill for {approval.PatientName}: {approval.Medication}", hubContext);

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"🔔 [approval] Reminder #{approval.ReminderCount} sent to doctor for {approval.PatientName}");

                return Results.Ok();
            });

            // Doctor approves/denies - Temporal version
            app.MapPost("/api/approve/{approvalId}", async (
                string approvalId,
                bool approved,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.ApprovalRequests.TryGetValue(approvalId, out var approval))
                {
                    return Results.NotFound();
                }

                if (!DataStore.Prescriptions.TryGetValue(approval.PrescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                // Send signal to Temporal workflow
                var workflowId = $"prescription-{prescription.PatientId}-{approval.PrescriptionId}";
                var handle = client.GetWorkflowHandle(workflowId);

                if (approved)
                {
                    await handle.SignalAsync((PrescriptionWorkflow wf) => wf.ApproveAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [temporal] Doctor approved workflow for {prescription.PatientName}");

                    // Update local state
                    approval.IsApproved = true;
                    prescription.Status = "Approved";
                    prescription.RefillsRemaining = 3;
                }
                else
                {
                    await handle.SignalAsync((PrescriptionWorkflow wf) => wf.DenyAsync());
                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"❌ [temporal] Doctor denied workflow for {prescription.PatientName}");

                    // Update local state
                    approval.IsDenied = true;
                    prescription.Status = "Denied";
                }

                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ApprovalRequestUpdated", approval);

                return Results.Ok();
            });

            app.MapPost("/api/notify/{prescriptionId}", async (
                string prescriptionId,
                string recipient,
                string recipientName,
                string message,
                IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                // Mark as retrying before attempting
                prescription.NotificationStatus = "Retrying";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                await EndpointHelper.SimulateEndpointBehavior("notify", hubContext);

                // Success
                prescription.NotificationStatus = "Sent";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await EndpointHelper.SendNotification(recipient, recipientName, message, hubContext);
                return Results.Ok();
            });

            app.MapPost("/api/notify-failed/{prescriptionId}", async (
                string prescriptionId,
                IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }
                prescription.NotificationStatus = "Failed";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                return Results.Ok();
            });

            // STEP 5: Submit to Pharmacy
            app.MapPost("/api/submit/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {

                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                prescription.Status = "Submitting";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [submit] Submitting to pharmacy for {prescription.PatientName}");
                await EndpointHelper.SimulateEndpointBehavior("submit", hubContext);

                await Task.Delay(100);

                prescription.Status = "Completed";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                await EndpointHelper.SendNotification("patient", prescription.PatientName,
                    $"Your prescription for {prescription.Medication} has been sent to your pharmacy. Copay: ${prescription.Copay:F2}", hubContext);

                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"✅ [submit] Prescription completed for {prescription.PatientName}");

                return Results.Ok(new { submitted = true });
            });

            // Generate load for testing (20 prescriptions)
            app.MapPost("/api/admin/generate-load", async (IHubContext<NotificationHub> hubContext) =>
            {
                var medications = new[] { "Lipitor", "Metformin", "Lisinopril", "Atorvastatin", "Omeprazole", "Amlodipine", "Simvastatin" };
                var firstNames = new[] { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Barbara" };
                var lastNames = new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez" };

                for (int i = 0; i < 20; i++)
                {
                    // Only 2-3 prescriptions should be not eligible yet
                    DateTime eligibleDate;
                    if (i < 2)
                    {
                        // First 2: Not eligible yet (1-2 minutes in the future)
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(1, 3));
                    }
                    else
                    {
                        // Rest: Already eligible (in the past)
                        eligibleDate = DateTime.UtcNow.AddMinutes(Random.Shared.Next(-120, 0));
                    }

                    var prescription = new Prescription
                    {
                        PatientId = $"P{100 + i}",
                        PatientName = $"{firstNames[Random.Shared.Next(firstNames.Length)]} {lastNames[Random.Shared.Next(lastNames.Length)]}",
                        Medication = $"{medications[Random.Shared.Next(medications.Length)]} {Random.Shared.Next(10, 80)}mg",
                        EligibleDate = eligibleDate,
                        RefillsRemaining = Random.Shared.Next(0, 4),
                        Status = "Pending"
                    };

                    DataStore.Prescriptions[prescription.Id] = prescription;
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                }

                await hubContext.Clients.All.SendAsync("ReceiveLog", $"📦 [admin] Generated 20 test prescriptions");
                return Results.Ok(new { generated = 20 });
            });

            // Reset all data
            app.MapPost("/api/admin/reset", async (IHubContext<NotificationHub> hubContext) =>
            {
                // Clear all prescriptions except the initial 5
                DataStore.Prescriptions.Clear();
                DataStore.ApprovalRequests.Clear();

                // Re-populate with initial data
                var prescriptions = new[]
                {
                    new Prescription
                    {
                        PatientId = "P001",
                        PatientName = "John Smith",
                        Medication = "Lipitor 20mg",
                        EligibleDate = DateTime.UtcNow.AddDays(-1),
                        RefillsRemaining = 3,
                        Status = "Pending"
                    },
                    new Prescription
                    {
                        PatientId = "P002",
                        PatientName = "Michael Davis",
                        Medication = "Omeprazole 20mg",
                        EligibleDate = DateTime.UtcNow.AddMinutes(2),
                        RefillsRemaining = 5,
                        Status = "Pending"
                    },
                    new Prescription
                    {
                        PatientId = "P003",
                        PatientName = "Mary Johnson",
                        Medication = "Metformin 500mg",
                        EligibleDate = DateTime.UtcNow.AddHours(-2),
                        RefillsRemaining = 2,
                        Status = "Pending"
                    },
                    new Prescription
                    {
                        PatientId = "P004",
                        PatientName = "Robert Williams",
                        Medication = "Lisinopril 10mg",
                        EligibleDate = DateTime.UtcNow.AddDays(-5),
                        RefillsRemaining = 0,
                        Status = "Pending"
                    },
                    new Prescription
                    {
                        PatientId = "P005",
                        PatientName = "Patricia Brown",
                        Medication = "Atorvastatin 40mg",
                        EligibleDate = DateTime.UtcNow.AddDays(-3),
                        RefillsRemaining = 1,
                        Status = "Pending"
                    },
                };

                foreach (var rx in prescriptions)
                {
                    DataStore.Prescriptions[rx.Id] = rx;
                    await hubContext.Clients.All.SendAsync("PrescriptionUpdated", rx);
                }

                await hubContext.Clients.All.SendAsync("ReceiveLog", $"🔄 [admin] System reset - restored 5 default prescriptions");
                return Results.Ok(new { reset = true });
            });

            // NEW: Start Temporal workflow
            app.MapPost("/api/workflow/start/{prescriptionId}", async (
                string prescriptionId,
                [FromServices] ITemporalClient client,
                IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                var input = new PrescriptionInput
                {
                    PrescriptionId = prescriptionId,
                    PatientName = prescription.PatientName,
                    Medication = prescription.Medication,
                    EligibleDate = prescription.EligibleDate,
                    RefillsRemaining = prescription.RefillsRemaining
                };

                // Start the workflow
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

            app.MapPost("/api/prescriptions", async (Prescription prescription, IHubContext<NotificationHub> hubContext) =>
            {
                DataStore.Prescriptions[prescription.Id] = prescription;
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📝 [admin] Created new prescription for {prescription.PatientName}");
                return Results.Ok(prescription);
            });

            // Handle approval timeout
            app.MapPost("/api/approval-timeout/{prescriptionId}", async (
                string prescriptionId,
                IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                // Find and mark approval request as timed out
                var approval = DataStore.ApprovalRequests.Values
                    .FirstOrDefault(a => a.PrescriptionId == prescriptionId && !a.IsApproved && !a.IsDenied);

                if (approval != null)
                {
                    approval.IsDenied = true; // Mark as denied due to timeout
                    await hubContext.Clients.All.SendAsync("ApprovalRequestUpdated", approval);
                }

                prescription.Status = "ApprovalTimeout";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"⏰ [approval] Approval timeout for {prescription.PatientName} - no response from doctor");

                return Results.Ok();
            });

            // Mark prescription on hold (submission failed)
            app.MapPost("/api/on-hold/{prescriptionId}", async (
                string prescriptionId,
                IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                prescription.Status = "OnHold";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"⚠️ [admin] Prescription on hold for {prescription.PatientName} - pharmacy submission failed, awaiting manual intervention");

                return Results.Ok();
            });

            app.Run();
        }
    }
}