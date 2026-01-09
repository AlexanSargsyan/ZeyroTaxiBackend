using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Taxi_API.Services
{
    public class FcmService : IFcmService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<FcmService> _logger;
        private readonly HttpClient _http;
        private readonly string? _serverKey;

        public FcmService(IConfiguration config, ILogger<FcmService> logger)
        {
            _config = config;
            _logger = logger;
            _http = new HttpClient();

            _serverKey = _config["Fcm:ServerKey"];
            if (string.IsNullOrWhiteSpace(_serverKey))
            {
                _logger.LogWarning("FCM server key not configured. Push notifications will be disabled.");
            }
        }

        public async Task SendPushAsync(string deviceToken, string title, string body, Dictionary<string, string>? data = null)
        {
            if (string.IsNullOrWhiteSpace(_serverKey))
            {
                _logger.LogInformation("FCM ServerKey not configured, skipping push to {token}", deviceToken);
                return;
            }

            var payload = new Dictionary<string, object>
            {
                ["to"] = deviceToken,
                ["notification"] = new { title, body },
            };

            if (data != null && data.Count > 0)
            {
                payload["data"] = data;
            }

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("key", _serverKey);

            try
            {
                var res = await _http.PostAsync("https://fcm.googleapis.com/fcm/send", content);
                if (!res.IsSuccessStatusCode)
                {
                    var resp = await res.Content.ReadAsStringAsync();
                    _logger.LogWarning("FCM send failed: {status} {resp}", res.StatusCode, resp);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send FCM message");
            }
        }
    }
}
