using Temporalio.Activities;
using PBMAdjudication.Core;
using Microsoft.Extensions.Configuration;

namespace PBMAdjudication.Worker
{
    public class PrescriptionActivities
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _baseUrl;  // http://proxy:5003 — fault-injectable business calls
        private readonly string _apiUrl;   // http://api:5002  — direct bookkeeping calls, never injected

        public PrescriptionActivities(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _baseUrl = configuration["BaseUrl"] ?? "http://localhost:5002";
            _apiUrl  = configuration["ApiUrl"]  ?? _baseUrl; // fallback for local dev
        }

        // ── HTTP helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Business activity call — routed through the proxy so the Network Console
        /// can inject faults. On non-2xx, fires a best-effort retrying notification
        /// then throws so Temporal owns the retry cycle. On success, clears retry state.
        /// </summary>
        private async Task<HttpResponseMessage> PostThroughProxyAsync(
            string prescriptionId, string step, string url, HttpContent? content = null)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                _ = client.PostAsync(
                    $"{_apiUrl}/api/activity-retrying/{prescriptionId}/{step}", null);
                throw new ApplicationException($"{step} failed: {response.StatusCode}");
            }

            _ = client.PostAsync(
                $"{_apiUrl}/api/activity-retry-cleared/{prescriptionId}", null);

            return response;
        }

        /// <summary>
        /// Internal bookkeeping call — goes directly to the API, bypassing the proxy.
        /// Used for status updates, timeout handlers, and on-hold marking that should
        /// always succeed regardless of fault injection state.
        /// </summary>
        private async Task PostDirectAsync(string url)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync(url, null);
            if (!response.IsSuccessStatusCode)
                throw new ApplicationException($"Internal API call failed: {response.StatusCode} — {url}");
        }

        // ── Business activities (routed through proxy) ────────────────────────

        [Activity]
        public async Task<ValidationResult> ValidateEligibilityAsync(string prescriptionId, string? imageData = null)
        {
            if (imageData != null)
            {
                var sizeKb = (imageData.Length * 3 / 4) / 1024;
                Console.WriteLine($"[validate] Insurance card / Rx image attached (~{sizeKb} KB) — using for eligibility verification");
            }

            var response = await PostThroughProxyAsync(prescriptionId, "validate",
                $"{_baseUrl}/api/validate/{prescriptionId}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new ValidationResult
            {
                Eligible = root.TryGetProperty("eligible", out var eligibleProp) && eligibleProp.GetBoolean(),
                EligibleDate = root.TryGetProperty("waitUntil", out var waitProp) ? waitProp.GetDateTime() : null
            };
        }

        [Activity]
        public async Task<AuthorizationResult> CheckPriorAuthorizationAsync(string prescriptionId)
        {
            var response = await PostThroughProxyAsync(prescriptionId, "authorize",
                $"{_baseUrl}/api/authorize/{prescriptionId}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AuthorizationResult
            {
                Authorized = root.TryGetProperty("authorized", out var authProp) && authProp.GetBoolean()
            };
        }

        [Activity]
        public async Task<AdjudicationResult> AdjudicateClaimAsync(string prescriptionId)
        {
            var response = await PostThroughProxyAsync(prescriptionId, "adjudicate",
                $"{_baseUrl}/api/adjudicate/{prescriptionId}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AdjudicationResult
            {
                Copay = root.TryGetProperty("copay", out var copayProp) ? copayProp.GetDecimal() : 0
            };
        }

        [Activity]
        public async Task<ApprovalResult> RequestDoctorApprovalAsync(string prescriptionId)
        {
            var response = await PostThroughProxyAsync(prescriptionId, "request-approval",
                $"{_baseUrl}/api/request-approval/{prescriptionId}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new ApprovalResult
            {
                ApprovalNeeded = root.TryGetProperty("approvalNeeded", out var neededProp) && neededProp.GetBoolean(),
                ApprovalId = root.TryGetProperty("approvalId", out var idProp) ? idProp.GetString() : null
            };
        }

        [Activity]
        public async Task SendNotificationAsync(string prescriptionId, string recipient, string recipientName, string message)
        {
            // Notification goes through the proxy so it can be fault-injected independently.
            // Uses its own "notify" step key — NotificationStatus is tracked separately
            // from ActivityRetryStatus so the two don't collide.
            var response = await PostThroughProxyAsync(prescriptionId, "notify",
                $"{_baseUrl}/api/notify/{prescriptionId}?recipient={recipient}&recipientName={Uri.EscapeDataString(recipientName)}&message={Uri.EscapeDataString(message)}");

            if (!response.IsSuccessStatusCode)
                throw new ApplicationException($"notify failed: {response.StatusCode}");
        }

        [Activity]
        public async Task<SubmissionResult> SubmitToPharmacyAsync(string prescriptionId)
        {
            var response = await PostThroughProxyAsync(prescriptionId, "submit",
                $"{_baseUrl}/api/submit/{prescriptionId}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new SubmissionResult
            {
                Submitted = root.TryGetProperty("submitted", out var submittedProp) && submittedProp.GetBoolean()
            };
        }

        // ── Bookkeeping activities (direct to API, not fault-injectable) ──────

        [Activity]
        public async Task HandleApprovalTimeoutAsync(string prescriptionId)
        {
            await PostDirectAsync($"{_apiUrl}/api/approval-timeout/{prescriptionId}");
        }

        [Activity]
        public async Task MarkOnHoldAsync(string prescriptionId, int failedStep)
        {
            await PostDirectAsync($"{_apiUrl}/api/on-hold/{prescriptionId}?failedStep={failedStep}");
        }

        [Activity]
        public async Task MarkNotificationFailedAsync(string prescriptionId)
        {
            // Best-effort — swallow errors, workflow continues regardless
            try { await PostDirectAsync($"{_apiUrl}/api/notify-failed/{prescriptionId}"); }
            catch { /* intentionally silent */ }
        }

        // ── GLP-1 specialty activities (v2+) ─────────────────────────────────

        [Activity]
        public async Task<AdjudicationResult> AdjudicateGlp1ClaimAsync(string prescriptionId)
        {
            var response = await PostThroughProxyAsync(prescriptionId, "adjudicate-glp1",
                $"{_baseUrl}/api/adjudicate-glp1/{prescriptionId}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AdjudicationResult
            {
                Copay = root.TryGetProperty("copay", out var copayProp) ? copayProp.GetDecimal() : 0
            };
        }

        [Activity]
        public async Task RequestSpecialtyPriorAuthAsync(string prescriptionId, string patientName, string medication)
        {
            await PostThroughProxyAsync(prescriptionId, "request-specialty-auth",
                $"{_baseUrl}/api/request-specialty-auth/{prescriptionId}" +
                $"?patientName={Uri.EscapeDataString(patientName)}&medication={Uri.EscapeDataString(medication)}");
        }

        [Activity]
        public async Task HandleSpecialtyAuthTimeoutAsync(string prescriptionId)
        {
            await PostDirectAsync($"{_apiUrl}/api/specialty-auth-timeout/{prescriptionId}");
        }

        [Activity]
        public async Task<SubmissionResult> SubmitToSpecialtyPharmacyAsync(string prescriptionId)
        {
            var response = await PostThroughProxyAsync(prescriptionId, "submit-specialty",
                $"{_baseUrl}/api/submit-specialty/{prescriptionId}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new SubmissionResult
            {
                Submitted = root.TryGetProperty("submitted", out var submittedProp) && submittedProp.GetBoolean()
            };
        }
    }
}
