using Temporalio.Activities;
using PBMAdjudication.Core;
using Microsoft.Extensions.Configuration;

namespace PBMAdjudication.Worker
{
    public class PrescriptionActivities
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _baseUrl;

        public PrescriptionActivities(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _baseUrl = configuration["BaseUrl"] ?? "http://localhost:5002";
        }

        [Activity]
        public async Task<ValidationResult> VerifyAccountAsync(string prescriptionId, string? imageData = null)
        {
            // If an image was attached, log its presence. In a real PBM system this would
            // be the insurance card or Rx scan used to verify eligibility. The Claim Check
            // codec has already replaced large payloads with a storage token before this
            // activity input was written to Temporal history — so what arrived here is
            // either the raw base64 string (small image, under threshold) or the original
            // bytes fetched back from the store (large image, token resolved by codec).
            if (imageData != null)
            {
                var sizeKb = (imageData.Length * 3 / 4) / 1024;
                Console.WriteLine($"[validate] Insurance card / Rx image attached (~{sizeKb} KB) — using for eligibility verification");
            }

            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/validate/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Validation failed: {response.StatusCode}");
            }

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
        public async Task<AuthorizationResult> ScreenSanctionsAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/authorize/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Authorization failed: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AuthorizationResult
            {
                Authorized = root.TryGetProperty("authorized", out var authProp) && authProp.GetBoolean()
            };
        }

        [Activity]
        public async Task<AdjudicationResult> CalculateFxFeesAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/adjudicate/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Adjudication failed: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AdjudicationResult
            {
                Copay = root.TryGetProperty("copay", out var copayProp) ? copayProp.GetDecimal() : 0
            };
        }

        [Activity]
        public async Task<ApprovalResult> RequestComplianceReviewAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/request-approval/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Approval request failed: {response.StatusCode}");
            }

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
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync(
                $"{_baseUrl}/api/notify/{prescriptionId}?recipient={recipient}&recipientName={Uri.EscapeDataString(recipientName)}&message={Uri.EscapeDataString(message)}",
                null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Notification failed: {response.StatusCode}");
            }
        }

        [Activity]
        public async Task<SubmissionResult> SettlePaymentAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/submit/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Submission failed: {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new SubmissionResult
            {
                Submitted = root.TryGetProperty("submitted", out var submittedProp) && submittedProp.GetBoolean()
            };
        }

        [Activity]
        public async Task HandleReviewTimeoutAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/approval-timeout/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Timeout handling failed: {response.StatusCode}");
            }
        }

        [Activity]
        public async Task MarkOnHoldAsync(string prescriptionId, int failedStep)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/on-hold/{prescriptionId}?failedStep={failedStep}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Failed to mark on hold: {response.StatusCode}");
            }
        }

        [Activity]
        public async Task MarkNotificationFailedAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            await client.PostAsync($"{_baseUrl}/api/notify-failed/{prescriptionId}", null);
        }

        // ── GLP-1 specialty activities (v2+) ─────────────────────────────────

        [Activity]
        public async Task<AdjudicationResult> AdjudicateEddClaimAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/adjudicate-glp1/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
                throw new ApplicationException($"GLP-1 adjudication failed: {response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AdjudicationResult
            {
                Copay = root.TryGetProperty("copay", out var copayProp) ? copayProp.GetDecimal() : 0
            };
        }

        [Activity]
        public async Task RequestEddReviewAsync(string prescriptionId, string patientName, string medication)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync(
                $"{_baseUrl}/api/request-specialty-auth/{prescriptionId}" +
                $"?patientName={Uri.EscapeDataString(patientName)}&medication={Uri.EscapeDataString(medication)}",
                null);

            if (!response.IsSuccessStatusCode)
                throw new ApplicationException($"Specialty auth request failed: {response.StatusCode}");
        }

        [Activity]
        public async Task HandleEddReviewTimeoutAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/specialty-auth-timeout/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
                throw new ApplicationException($"Specialty auth timeout handling failed: {response.StatusCode}");
        }

        [Activity]
        public async Task<SubmissionResult> SettleEnhancedRailAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/submit-specialty/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
                throw new ApplicationException($"Specialty pharmacy submission failed: {response.StatusCode}");

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
