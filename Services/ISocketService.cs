using System.Net.WebSockets;

namespace Taxi_API.Services
{
    public interface ISocketService
    {
        Task HandleRawSocketAsync(Guid userId, string? role, WebSocket socket);
        Task BroadcastCarLocationAsync(Guid orderId, double lat, double lng);
        Task NotifyOrderEventAsync(Guid orderId, string eventName, object? payload = null);
    }
}