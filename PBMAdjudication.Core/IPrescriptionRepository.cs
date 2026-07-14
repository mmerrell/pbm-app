using System.Collections.Generic;
using System.Threading.Tasks;

namespace PBMAdjudication.Core
{
    public interface IPrescriptionRepository
    {
        // Prescriptions
        Task<IEnumerable<Prescription>> GetAllPrescriptionsAsync();
        Task<Prescription?> GetPrescriptionAsync(string id);
        Task UpsertPrescriptionAsync(Prescription prescription);
        Task<IEnumerable<Prescription>> GetPendingPrescriptionsAsync();

        // Approval Requests
        Task<DoctorApprovalRequest?> GetApprovalRequestAsync(string id);
        Task<DoctorApprovalRequest?> GetApprovalRequestByPrescriptionAsync(string prescriptionId);
        Task<IEnumerable<DoctorApprovalRequest>> GetPendingApprovalRequestsAsync();
        Task UpsertApprovalRequestAsync(DoctorApprovalRequest request);

        // Specialty Approval Requests (EDD, v2+)
        Task<SpecialtyApprovalRequest?> GetSpecialtyApprovalRequestAsync(string id);
        Task<SpecialtyApprovalRequest?> GetSpecialtyApprovalRequestByPrescriptionAsync(string prescriptionId);
        Task<IEnumerable<SpecialtyApprovalRequest>> GetPendingSpecialtyApprovalRequestsAsync();
        Task UpsertSpecialtyApprovalRequestAsync(SpecialtyApprovalRequest request);

        // Endpoint Configs
        Task<EndpointConfig> GetEndpointConfigAsync(string endpoint);
        Task<Dictionary<string, EndpointConfig>> GetAllEndpointConfigsAsync();
        Task UpsertEndpointConfigAsync(string endpoint, EndpointConfig config);

        // Admin
        Task ResetAsync();
    }
}