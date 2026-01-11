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

        public record AddCardRequest(string CardNumber, int ExpMonth, int ExpYear, string Cvc, bool MakeDefault = false);

        [Authorize]
        [HttpPost("cards")]
        public async Task<IActionResult> AddCard([FromBody] AddCardRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.CardNumber)) return BadRequest("Card data required");

            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            // Tokenize via payment provider
            var token = await _payments.TokenizeCardAsync(req.CardNumber, req.ExpMonth, req.ExpYear, req.Cvc);

            // Save masked card info
            var last4 = req.CardNumber.Length >= 4 ? req.CardNumber[^4..] : req.CardNumber;
            var card = new PaymentCard { UserId = userId, TokenHash = token, Last4 = last4, Brand = null, ExpMonth = req.ExpMonth, ExpYear = req.ExpYear, IsDefault = req.MakeDefault };
            if (req.MakeDefault)
            {
                var existing = await _db.PaymentCards.Where(c => c.UserId == userId).ToListAsync();
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
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var cards = await _db.PaymentCards.Where(c => c.UserId == userId).Select(c => new { id = c.Id, last4 = c.Last4, brand = c.Brand, expMonth = c.ExpMonth, expYear = c.ExpYear, isDefault = c.IsDefault }).ToListAsync();
            return Ok(cards);
        }

        public record ChargeRequest(decimal Amount, string Currency);

        [Authorize]
        [HttpPost("charge")]
        public async Task<IActionResult> ChargeDefault([FromBody] ChargeRequest req)
        {
            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

            var card = await _db.PaymentCards.FirstOrDefaultAsync(c => c.UserId == userId && c.IsDefault);
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
