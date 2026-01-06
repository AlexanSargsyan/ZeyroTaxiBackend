using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taxi_API.Data;
using Taxi_API.Models;

namespace Taxi_API.Services
{
    public class WebSocketService : ISocketService
    {
        private readonly ILogger<WebSocketService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        // store active sockets per user/driver
        private readonly Dictionary<Guid, WebSocket> _sockets = new();

        // map orderId to driver userId (if assigned)
        private readonly Dictionary<Guid, Guid> _orderDriverMap = new();

        public WebSocketService(ILogger<WebSocketService> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        public async Task HandleRawSocketAsync(Guid userId, string? role, WebSocket socket)
        {
            _sockets[userId] = socket;
            _logger.LogInformation("Socket connected for {userId} role={role}", userId, role);

            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    // Expect simple JSON messages from client, handle ping or other client commands
                    try
                    {
                        var doc = JsonDocument.Parse(msg);
                        if (doc.RootElement.TryGetProperty("type", out var t))
                        {
                            var type = t.GetString();
                            if (type == "registerOrder" && doc.RootElement.TryGetProperty("orderId", out var oid))
                            {
                                if (Guid.TryParse(oid.GetString(), out var orderId) && role == "driver")
                                {
                                    _orderDriverMap[orderId] = userId;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                _sockets.Remove(userId);
                _logger.LogInformation("Socket disconnected for {userId}", userId);
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
            }
        }

        public async Task BroadcastCarLocationAsync(Guid orderId, double lat, double lng)
        {
            // find rider for this order using scoped DB
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;

            var data = new { type = "carLocation", orderId = orderId, lat, lng };
            var json = JsonSerializer.Serialize(data);
            var buf = Encoding.UTF8.GetBytes(json);

            // send to rider
            if (order.UserId != Guid.Empty && _sockets.TryGetValue(order.UserId, out var ws) && ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
            }

            // also send to driver if known
            if (_orderDriverMap.TryGetValue(orderId, out var driverId) && _sockets.TryGetValue(driverId, out var dws) && dws.State == WebSocketState.Open)
            {
                await dws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public async Task NotifyOrderEventAsync(Guid orderId, string eventName, object? payload = null)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;

            var data = new { type = eventName, orderId = orderId, payload };
            var json = JsonSerializer.Serialize(data);
            var buf = Encoding.UTF8.GetBytes(json);

            // notify rider
            if (order.UserId != Guid.Empty && _sockets.TryGetValue(order.UserId, out var ws) && ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
            }

            // notify driver
            if (_orderDriverMap.TryGetValue(orderId, out var driverId) && _sockets.TryGetValue(driverId, out var dws) && dws.State == WebSocketState.Open)
            {
                await dws.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}
