namespace Taxi_API.Services
{
    public interface IPaymentService
    {
        // Tokenize card details and return a token string (should be sent to client to store securely)
        Task<string> TokenizeCardAsync(string cardNumber, int expMonth, int expYear, string cvc);

        // Charge a token for a given amount (in smallest currency unit), returns charge id
        Task<string> ChargeAsync(string token, long amountCents, string currency = "USD");

        // Refund a charge
        Task RefundAsync(string chargeId);

        // Create a PaymentIntent (for Stripe Elements / Payment Intents flow). Returns client_secret.
        Task<string?> CreatePaymentIntentAsync(long amountCents, string currency = "usd", string? customerId = null);
    }
}
