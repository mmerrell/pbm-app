using Temporalio.Activities;
using PBMAdjudicationService.Models;

namespace PBMAdjudicationService.Activities
{
    public class PrescriptionActivities
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _baseUrl;

        public PrescriptionActivities(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _baseUrl = configuration["BaseUrl"] ?? "http://localhost:5188";
        }

        [Activity]
        public async Task<ValidationResult> ValidateEligibilityAsync(string prescriptionId)
        {
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
        public async Task<AuthorizationResult> CheckPriorAuthorizationAsync(string prescriptionId)
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
        public async Task<AdjudicationResult> AdjudicateClaimAsync(string prescriptionId)
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
        public async Task<ApprovalResult> RequestDoctorApprovalAsync(string prescriptionId)
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
        public async Task<SubmissionResult> SubmitToPharmacyAsync(string prescriptionId)
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
        public async Task HandleApprovalTimeoutAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/approval-timeout/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Timeout handling failed: {response.StatusCode}");
            }
        }

        [Activity]
        public async Task MarkOnHoldAsync(string prescriptionId)
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsync($"{_baseUrl}/api/on-hold/{prescriptionId}", null);

            if (!response.IsSuccessStatusCode)
            {
                throw new ApplicationException($"Failed to mark on hold: {response.StatusCode}");
            }
        }
    }
}