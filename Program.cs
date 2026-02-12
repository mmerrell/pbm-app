using Microsoft.AspNetCore.SignalR;

namespace PBMAdjudicationService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

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
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await EndpointHelper.SimulateEndpointBehavior("validate", hubContext);

                        if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                        {
                            return Results.NotFound();
                        }

                        prescription.Status = "Validating";
                        await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                        await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [validate] Checking eligibility for {prescription.PatientName}");

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
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 3)
                        {
                            await hubContext.Clients.All.SendAsync("ReceiveLog", $"💥 [validate] Failed after 3 attempts: {ex.Message}");
                            throw;
                        }

                        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await hubContext.Clients.All.SendAsync("ReceiveLog",
                            $"⚠️ [validate] Attempt {attempt} failed, retrying in {backoff.TotalSeconds}s...");
                        await Task.Delay(backoff);
                    }
                }

                return Results.Problem("Validation failed");
            });

            // STEP 2: Check Prior Authorization
            app.MapPost("/api/authorize/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await EndpointHelper.SimulateEndpointBehavior("authorize", hubContext);

                        if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                        {
                            return Results.NotFound();
                        }

                        prescription.Status = "Authorizing";
                        await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                        await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Checking prior authorization for {prescription.Medication}");

                        await Task.Delay(100);

                        prescription.Status = "Authorized";
                        await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                        await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [authorize] Authorization approved for {prescription.Medication}");

                        return Results.Ok(new { authorized = true });
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 3)
                        {
                            await hubContext.Clients.All.SendAsync("ReceiveLog", $"💥 [authorize] Failed after 3 attempts: {ex.Message}");
                            throw;
                        }

                        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await hubContext.Clients.All.SendAsync("ReceiveLog",
                            $"⚠️ [authorize] Attempt {attempt} failed, retrying in {backoff.TotalSeconds}s...");
                        await Task.Delay(backoff);
                    }
                }

                return Results.Problem("Authorization failed");
            });

            // STEP 3: Adjudicate Claim
            app.MapPost("/api/adjudicate/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await EndpointHelper.SimulateEndpointBehavior("adjudicate", hubContext);

                        if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                        {
                            return Results.NotFound();
                        }

                        prescription.Status = "Adjudicating";
                        await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                        await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [adjudicate] Calculating copay for {prescription.Medication}");

                        await Task.Delay(100);
                        prescription.Copay = Random.Shared.Next(5, 50);

                        prescription.Status = "Adjudicated";
                        await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                        await hubContext.Clients.All.SendAsync("ReceiveLog",
                            $"✅ [adjudicate] Copay calculated: ${prescription.Copay:F2} for {prescription.PatientName}");

                        return Results.Ok(new { copay = prescription.Copay });
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 3)
                        {
                            await hubContext.Clients.All.SendAsync("ReceiveLog", $"💥 [adjudicate] Failed after 3 attempts: {ex.Message}");
                            throw;
                        }

                        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await hubContext.Clients.All.SendAsync("ReceiveLog",
                            $"⚠️ [adjudicate] Attempt {attempt} failed, retrying in {backoff.TotalSeconds}s...");
                        await Task.Delay(backoff);
                    }
                }

                return Results.Problem("Adjudication failed");
            });

            // STEP 4: Request Doctor Approval (if needed)
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

                var approvalRequest = new DoctorApprovalRequest
                {
                    PrescriptionId = prescriptionId,
                    PatientName = prescription.PatientName,
                    Medication = prescription.Medication
                };

                // Approval needed
                prescription.ApprovalNeededReason = "NO_REFILLS";
                DataStore.ApprovalRequests[approvalRequest.Id] = approvalRequest;

                prescription.Status = "ApprovalNeeded";
                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
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

            // Doctor approves/denies
            app.MapPost("/api/approve/{approvalId}", async (string approvalId, bool approved, IHubContext<NotificationHub> hubContext) =>
            {
                if (!DataStore.ApprovalRequests.TryGetValue(approvalId, out var approval))
                {
                    return Results.NotFound();
                }

                if (!DataStore.Prescriptions.TryGetValue(approval.PrescriptionId, out var prescription))
                {
                    return Results.NotFound();
                }

                if (approved)
                {
                    approval.IsApproved = true;
                    prescription.Status = "Approved";
                    prescription.RefillsRemaining = 3;

                    await EndpointHelper.SendNotification("patient", prescription.PatientName,
                        $"Good news! Your doctor approved your refill for {prescription.Medication}", hubContext);

                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"✅ [approval] Doctor approved refill for {prescription.PatientName}");
                }
                else
                {
                    approval.IsDenied = true;
                    prescription.Status = "Denied";

                    await EndpointHelper.SendNotification("patient", prescription.PatientName,
                        $"Your refill request for {prescription.Medication} was denied. Please contact your doctor.", hubContext);

                    await hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"❌ [approval] Doctor denied refill for {prescription.PatientName}");
                }

                await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                await hubContext.Clients.All.SendAsync("ApprovalRequestUpdated", approval);

                return Results.Ok();
            });

            // STEP 5: Submit to Pharmacy
            app.MapPost("/api/submit/{prescriptionId}", async (string prescriptionId, IHubContext<NotificationHub> hubContext) =>
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await EndpointHelper.SimulateEndpointBehavior("submit", hubContext);

                        if (!DataStore.Prescriptions.TryGetValue(prescriptionId, out var prescription))
                        {
                            return Results.NotFound();
                        }

                        prescription.Status = "Submitting";
                        await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);
                        await hubContext.Clients.All.SendAsync("ReceiveLog", $"✅ [submit] Submitting to pharmacy for {prescription.PatientName}");

                        await Task.Delay(100);

                        prescription.Status = "Completed";
                        await hubContext.Clients.All.SendAsync("PrescriptionUpdated", prescription);

                        await EndpointHelper.SendNotification("patient", prescription.PatientName,
                            $"Your prescription for {prescription.Medication} has been sent to your pharmacy. Copay: ${prescription.Copay:F2}", hubContext);

                        await hubContext.Clients.All.SendAsync("ReceiveLog",
                            $"✅ [submit] Prescription completed for {prescription.PatientName}");

                        return Results.Ok(new { submitted = true });
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 3)
                        {
                            await hubContext.Clients.All.SendAsync("ReceiveLog", $"💥 [submit] Failed after 3 attempts: {ex.Message}");
                            throw;
                        }

                        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await hubContext.Clients.All.SendAsync("ReceiveLog",
                            $"⚠️ [submit] Attempt {attempt} failed, retrying in {backoff.TotalSeconds}s...");
                        await Task.Delay(backoff);
                    }
                }

                return Results.Problem("Submission failed");
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

            app.Run();
        }
    }
}