using PBMAdjudication.Core;
using Microsoft.AspNetCore.SignalR;

namespace PBMAdjudicationService
{
    public class EndpointHelper
    {
        private readonly IPrescriptionRepository _repository;
        private readonly IHubContext<NotificationHub> _hubContext;

        public EndpointHelper(IPrescriptionRepository repository, IHubContext<NotificationHub> hubContext)
        {
            _repository = repository;
            _hubContext = hubContext;
        }

        public async Task<bool> SimulateEndpointBehavior(string endpointName)
        {
            var config = await _repository.GetEndpointConfigAsync(endpointName);

            if (config.CompleteOutage)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"❌ [{endpointName}] Complete outage - service unavailable");
                throw new Exception($"{endpointName} service is down");
            }
            if (config.LatencyMs > 0)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"⏱️ [{endpointName}] Simulating {config.LatencyMs}ms latency");
                await Task.Delay(config.LatencyMs);
            }
            if (config.FailureRatePercent > 0)
            {
                var random = Random.Shared.Next(100);
                if (random < config.FailureRatePercent)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"❌ [{endpointName}] Random failure ({config.FailureRatePercent}% rate)");
                    throw new Exception($"{endpointName} failed randomly");
                }
            }
            return true;
        }

        public async Task SendNotification(string recipient, string recipientName, string message)
        {
            try
            {
                await SimulateEndpointBehavior("notify");
                await _hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"📧 [notify] Sent to {recipient} ({recipientName}): {message}");
            }
            catch (Exception ex)
            {
                await _hubContext.Clients.All.SendAsync("ReceiveLog",
                    $"❌ [notify] Failed to send notification: {ex.Message}");
            }
        }
    }
}