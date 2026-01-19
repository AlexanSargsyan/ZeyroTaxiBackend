using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Taxi_API.Services
{
    public class VeloconnectSmsService : ISmsService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<VeloconnectSmsService> _logger;
        private readonly HttpClient _httpClient;

        public VeloconnectSmsService(IConfiguration config, ILogger<VeloconnectSmsService> logger, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient();
        }

        public async Task SendSmsAsync(string toPhone, string body)
        {
            var username = _config["Veloconnect:Username"];
            var password = _config["Veloconnect:Password"];
            var originator = _config["Veloconnect:Originator"] ?? "TaxiApp";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("Veloconnect not configured. Username or Password missing. Skipping SMS to {Phone}", toPhone);
                return;
            }

            try
            {
                // Veloconnect API endpoint
                var url = "https://api.veloconnect.me/api/v1/sms/send";

                // Prepare request payload according to Veloconnect API
                var payload = new
                {
                    username = username,
                    password = password,
                    originator = originator,
                    recipient = toPhone,
                    message = body
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending SMS via Veloconnect to {Phone}", toPhone);

                var response = await _httpClient.PostAsync(url, content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("SMS sent successfully via Veloconnect to {Phone}. Response: {Response}", toPhone, responseBody);
                }
                else
                {
                    _logger.LogWarning("Failed to send SMS via Veloconnect to {Phone}. Status: {Status}, Response: {Response}", 
                        toPhone, response.StatusCode, responseBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending SMS via Veloconnect to {Phone}", toPhone);
                throw;
            }
        }
    }
}
