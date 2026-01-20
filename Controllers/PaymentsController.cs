using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taxi_API.Data;
using Taxi_API.Models;
using Taxi_API.Services;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PaymentsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IPaymentService _payments;

        public PaymentsController(AppDbContext db, IPaymentService payments)
        {
            _db = db;
            _payments = payments;
        }

        // Helper method to extract user ID from claims
        private Guid? GetUserIdFromClaims()
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                         ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                         ?? User.FindFirst("sub")?.Value
                         ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
            
            if (string.IsNullOrEmpty(userIdStr))
            {
                return null;
            }
            
            if (Guid.TryParse(userIdStr, out var userId))
            {
                return userId;
            }
            
            return null;
        }

        public record AddCardRequest(string CardNumber, int ExpMonth, int ExpYear, string Cvc, bool MakeDefault = false);

        [Authorize]
        [HttpPost("cards")]
        public async Task<IActionResult> AddCard([FromBody] AddCardRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.CardNumber)) return BadRequest("Card data required");

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            // Tokenize via payment provider
            var token = await _payments.TokenizeCardAsync(req.CardNumber, req.ExpMonth, req.ExpYear, req.Cvc);

            // Save masked card info
            var last4 = req.CardNumber.Length >= 4 ? req.CardNumber[^4..] : req.CardNumber;
            var card = new PaymentCard { UserId = userId.Value, TokenHash = token, Last4 = last4, Brand = null, ExpMonth = req.ExpMonth, ExpYear = req.ExpYear, IsDefault = req.MakeDefault };
            if (req.MakeDefault)
            {
                var existing = await _db.PaymentCards.Where(c => c.UserId == userId.Value).ToListAsync();
                foreach (var e in existing) e.IsDefault = false;
            }
            _db.PaymentCards.Add(card);
            await _db.SaveChangesAsync();

            return Ok(new { id = card.Id, last4 = card.Last4, expMonth = card.ExpMonth, expYear = card.ExpYear, isDefault = card.IsDefault });
        }

        [Authorize]
        [HttpGet("cards")]
        public async Task<IActionResult> ListCards()
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var cards = await _db.PaymentCards.Where(c => c.UserId == userId.Value).Select(c => new { id = c.Id, last4 = c.Last4, brand = c.Brand, expMonth = c.ExpMonth, expYear = c.ExpYear, isDefault = c.IsDefault }).ToListAsync();
            return Ok(cards);
        }

        public record CreatePaymentIntentRequest(decimal Amount, string Currency);
        public record CreatePaymentIntentResponse(string ClientSecret);

        [Authorize]
        [HttpPost("payment-intent")]
        public async Task<IActionResult> CreatePaymentIntent([FromBody] CreatePaymentIntentRequest req)
        {
            if (req == null || req.Amount <= 0) return BadRequest("Amount required");
            var amountCents = (long)(req.Amount * 100);
            var clientSecret = await _payments.CreatePaymentIntentAsync(amountCents, req.Currency ?? "usd");
            if (clientSecret == null) return StatusCode(502, "Payment provider error");
            return Ok(new CreatePaymentIntentResponse(clientSecret));
        }

        public record ConfirmPaymentRequest(string PaymentIntentId, string PaymentMethodId);
        [Authorize]
        [HttpPost("confirm")]
        public async Task<IActionResult> ConfirmPayment([FromBody] ConfirmPaymentRequest req)
        {
            // In production confirm via Stripe SDK or rely on client-side Elements to confirm using client_secret.
            return Ok(new { ok = true });
        }

        public record ChargeRequest(decimal Amount, string Currency);

        [Authorize]
        [HttpPost("charge")]
        public async Task<IActionResult> ChargeDefault([FromBody] ChargeRequest req)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var card = await _db.PaymentCards.FirstOrDefaultAsync(c => c.UserId == userId.Value && c.IsDefault);
            if (card == null) return BadRequest("No default card");

            // charge in cents
            var amountCents = (long)(req.Amount * 100);
            var chargeId = await _payments.ChargeAsync(card.TokenHash ?? string.Empty, amountCents, req.Currency ?? "USD");

            return Ok(new { chargeId });
        }

        [Authorize]
        [HttpPost("refund/{chargeId}")]
        public async Task<IActionResult> Refund(string chargeId)
        {
            if (string.IsNullOrWhiteSpace(chargeId)) return BadRequest("chargeId required");
            await _payments.RefundAsync(chargeId);
            return Ok(new { refunded = true });
        }
    }
}
