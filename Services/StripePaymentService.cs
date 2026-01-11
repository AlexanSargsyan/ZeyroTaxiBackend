using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Taxi_API.Services
{
    // Minimal Stripe integration stub. For production add Stripe.NET package and implement properly.
    public class StripePaymentService : IPaymentService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<StripePaymentService> _logger;

        public StripePaymentService(IConfiguration config, ILogger<StripePaymentService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<string> TokenizeCardAsync(string cardNumber, int expMonth, int expYear, string cvc)
        {
            _logger.LogInformation("TokenizeCard called (stub). Return masked token.");
            // DO NOT store raw card numbers in production. Use Stripe Elements / tokenization.
            await Task.Delay(10);
            return "tok_" + Guid.NewGuid().ToString("N");
        }

        public async Task<string> ChargeAsync(string token, long amountCents, string currency = "USD")
        {
            _logger.LogInformation("Charge called (stub) token={token} amount={amount}", token, amountCents);
            await Task.Delay(10);
            return "ch_" + Guid.NewGuid().ToString("N");
        }

        public async Task RefundAsync(string chargeId)
        {
            _logger.LogInformation("Refund called (stub) charge={chargeId}", chargeId);
            await Task.Delay(10);
        }

        public async Task<string?> CreatePaymentIntentAsync(long amountCents, string currency = "usd", string? customerId = null)
        {
            _logger.LogInformation("CreatePaymentIntentAsync called (stub) amount={amount} currency={currency}", amountCents, currency);
            // In production use Stripe SDK:
            // var options = new PaymentIntentCreateOptions { Amount = amountCents, Currency = currency, Customer = customerId, PaymentMethodTypes = new List<string> { "card" } };
            // var service = new PaymentIntentService();
            // var intent = await service.CreateAsync(options);
            // return intent.ClientSecret;
            await Task.Delay(10);
            return "pi_client_secret_placeholder_" + Guid.NewGuid().ToString("N");
        }
    }
}
