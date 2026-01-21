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

        /// <summary>
        /// Request model for adding a new payment card
        /// </summary>
        /// <param name="CardNumber">Credit card number (13-19 digits)</param>
        /// <param name="CardholderName">Name on the card</param>
        /// <param name="ExpMonth">Expiration month (1-12)</param>
        /// <param name="ExpYear">Expiration year (YYYY format)</param>
        /// <param name="Cvc">Card security code (CVV/CVC)</param>
        /// <param name="MakeDefault">Set this card as the default payment method</param>
        public record AddCardRequest(
            string CardNumber, 
            string CardholderName,
            int ExpMonth, 
            int ExpYear, 
            string Cvc, 
            bool MakeDefault = false
        );

        /// <summary>
        /// Add a new payment card
        /// </summary>
        /// <remarks>
        /// Tokenizes and saves a payment card for the authenticated user.
        /// The card number is tokenized via the payment provider and not stored directly.
        /// Only the last 4 digits and cardholder name are stored for display purposes.
        /// 
        /// **Request Body Example:**
        /// ```json
        /// {
        ///   "cardNumber": "4242424242424242",
        ///   "cardholderName": "John Doe",
        ///   "expMonth": 12,
        ///   "expYear": 2025,
        ///   "cvc": "123",
        ///   "makeDefault": true
        /// }
        /// ```
        /// 
        /// **Supported Card Brands:**
        /// - Visa (starts with 4)
        /// - Mastercard (starts with 5)
        /// - American Express (starts with 34 or 37)
        /// - Discover (starts with 6)
        /// 
        /// **Note:** In production, use client-side tokenization (e.g., Stripe Elements)
        /// to avoid sending raw card numbers to your server for PCI compliance.
        /// </remarks>
        /// <param name="req">Card information including number, name, expiration, and CVV</param>
        /// <returns>Card details with ID, last 4 digits, brand, and default status</returns>
        /// <response code="200">Card added successfully</response>
        /// <response code="400">Invalid card data (expired, invalid format, etc.)</response>
        /// <response code="401">User not authenticated</response>
        /// <response code="502">Payment provider error</response>
        [Authorize]
        [HttpPost("cards")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(typeof(object), 401)]
        [ProducesResponseType(typeof(object), 502)]
        public async Task<IActionResult> AddCard([FromBody] AddCardRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.CardNumber)) 
                return BadRequest("Card number is required");

            if (string.IsNullOrWhiteSpace(req.CardholderName))
                return BadRequest("Cardholder name is required");

            if (string.IsNullOrWhiteSpace(req.Cvc))
                return BadRequest("CVV/CVC is required");

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            // Validate card number format
            var cleanedCardNumber = new string(req.CardNumber.Where(char.IsDigit).ToArray());
            if (cleanedCardNumber.Length < 13 || cleanedCardNumber.Length > 19)
            {
                return BadRequest("Invalid card number length (must be 13-19 digits)");
            }

            // Validate CVV format (3-4 digits)
            var cleanedCvc = new string(req.Cvc.Where(char.IsDigit).ToArray());
            if (cleanedCvc.Length < 3 || cleanedCvc.Length > 4)
            {
                return BadRequest("Invalid CVV/CVC (must be 3-4 digits)");
            }

            // Validate expiration
            var now = DateTime.UtcNow;
            if (req.ExpYear < now.Year || (req.ExpYear == now.Year && req.ExpMonth < now.Month))
            {
                return BadRequest("Card is expired");
            }

            if (req.ExpMonth < 1 || req.ExpMonth > 12)
            {
                return BadRequest("Invalid expiration month (must be 1-12)");
            }

            // Validate cardholder name
            if (req.CardholderName.Length < 2 || req.CardholderName.Length > 50)
            {
                return BadRequest("Cardholder name must be 2-50 characters");
            }

            // Tokenize via payment provider
            var token = await _payments.TokenizeCardAsync(cleanedCardNumber, req.ExpMonth, req.ExpYear, cleanedCvc);

            if (string.IsNullOrWhiteSpace(token))
            {
                return StatusCode(502, "Failed to tokenize card with payment provider");
            }

            // Detect card brand from card number
            var brand = DetectCardBrand(cleanedCardNumber);

            // Save masked card info
            var last4 = cleanedCardNumber.Length >= 4 ? cleanedCardNumber[^4..] : cleanedCardNumber;
            var card = new PaymentCard 
            { 
                UserId = userId.Value, 
                TokenHash = token, 
                Last4 = last4, 
                Brand = brand,
                CardholderName = req.CardholderName,
                ExpMonth = req.ExpMonth, 
                ExpYear = req.ExpYear, 
                IsDefault = req.MakeDefault 
            };

            if (req.MakeDefault)
            {
                // Unset all other cards as default
                var existing = await _db.PaymentCards.Where(c => c.UserId == userId.Value).ToListAsync();
                foreach (var e in existing) e.IsDefault = false;
            }
            else
            {
                // If this is the first card, make it default
                var hasCards = await _db.PaymentCards.AnyAsync(c => c.UserId == userId.Value);
                if (!hasCards)
                {
                    card.IsDefault = true;
                }
            }

            _db.PaymentCards.Add(card);
            await _db.SaveChangesAsync();

            return Ok(new 
            { 
                id = card.Id, 
                last4 = card.Last4, 
                brand = card.Brand,
                cardholderName = card.CardholderName,
                expMonth = card.ExpMonth, 
                expYear = card.ExpYear, 
                isDefault = card.IsDefault,
                createdAt = card.CreatedAt
            });
        }

        /// <summary>
        /// Get all payment cards for the authenticated user
        /// </summary>
        /// <remarks>
        /// Retrieves a list of all payment cards saved by the authenticated user.
        /// Cards are ordered with the default card first, then by creation date (newest first).
        /// 
        /// **Response Example:**
        /// ```json
        /// [
        ///   {
        ///     "id": 1,
        ///     "last4": "4242",
        ///     "brand": "Visa",
        ///     "cardholderName": "John Doe",
        ///     "expMonth": 12,
        ///     "expYear": 2025,
        ///     "isDefault": true,
        ///     "createdAt": "2025-01-20T10:30:00Z"
        ///   },
        ///   {
        ///     "id": 2,
        ///     "last4": "5555",
        ///     "brand": "Mastercard",
        ///     "cardholderName": "Jane Smith",
        ///     "expMonth": 6,
        ///     "expYear": 2026,
        ///     "isDefault": false,
        ///     "createdAt": "2025-01-19T14:20:00Z"
        ///   }
        /// ]
        /// ```
        /// 
        /// **Card Information Includes:**
        /// - Card ID (for operations like set-default or delete)
        /// - Last 4 digits of card number
        /// - Card brand (Visa, Mastercard, Amex, Discover)
        /// - Cardholder name
        /// - Expiration date (month and year)
        /// - Default status
        /// - Creation timestamp
        /// </remarks>
        /// <returns>Array of card objects ordered by default status and creation date</returns>
        /// <response code="200">Successfully retrieved list of cards</response>
        /// <response code="401">User not authenticated</response>
        [Authorize]
        [HttpGet("cards")]
        [ProducesResponseType(typeof(object[]), 200)]
        [ProducesResponseType(typeof(object), 401)]
        public async Task<IActionResult> ListCards()
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var cards = await _db.PaymentCards
                .Where(c => c.UserId == userId.Value)
                .OrderByDescending(c => c.IsDefault)
                .ThenByDescending(c => c.CreatedAt)
                .Select(c => new 
                { 
                    id = c.Id, 
                    last4 = c.Last4, 
                    brand = c.Brand,
                    cardholderName = c.CardholderName,
                    expMonth = c.ExpMonth, 
                    expYear = c.ExpYear, 
                    isDefault = c.IsDefault,
                    createdAt = c.CreatedAt
                })
                .ToListAsync();

            return Ok(cards);
        }

        /// <summary>
        /// Set a card as the default payment method
        /// </summary>
        /// <remarks>
        /// Sets the specified card as the default payment method for the authenticated user.
        /// All other cards will automatically be marked as non-default.
        /// The default card is used for automatic payments and is displayed first in the card list.
        /// 
        /// **Request:**
        /// - Provide the card ID in the URL path
        /// - Card must belong to the authenticated user
        /// 
        /// **Response Example:**
        /// ```json
        /// {
        ///   "id": 2,
        ///   "isDefault": true,
        ///   "message": "Card set as default"
        /// }
        /// ```
        /// 
        /// **Use Cases:**
        /// - User wants to change their preferred payment method
        /// - Setting up a new card as primary
        /// - Switching between multiple saved cards
        /// </remarks>
        /// <param name="cardId">ID of the card to set as default</param>
        /// <returns>Confirmation with card ID and default status</returns>
        /// <response code="200">Card successfully set as default</response>
        /// <response code="401">User not authenticated</response>
        /// <response code="404">Card not found or doesn't belong to user</response>
        [Authorize]
        [HttpPut("cards/{cardId}/set-default")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 401)]
        [ProducesResponseType(typeof(object), 404)]
        public async Task<IActionResult> SetDefaultCard(int cardId)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var card = await _db.PaymentCards.FirstOrDefaultAsync(c => c.Id == cardId && c.UserId == userId.Value);
            if (card == null) return NotFound("Card not found");

            // Unset all other cards as default
            var allCards = await _db.PaymentCards.Where(c => c.UserId == userId.Value).ToListAsync();
            foreach (var c in allCards)
            {
                c.IsDefault = (c.Id == cardId);
            }

            await _db.SaveChangesAsync();

            return Ok(new { id = card.Id, isDefault = true, message = "Card set as default" });
        }

        /// <summary>
        /// Delete a payment card
        /// </summary>
        /// <remarks>
        /// Permanently deletes a payment card from the user's account.
        /// If the deleted card was the default, the most recently added card will automatically become the new default.
        /// 
        /// **Important Notes:**
        /// - This action cannot be undone
        /// - Card information is permanently removed from the database
        /// - If this is the only card, no default will be set
        /// - The payment provider token is also removed
        /// 
        /// **Request:**
        /// - Provide the card ID in the URL path
        /// - Card must belong to the authenticated user
        /// 
        /// **Response Example:**
        /// ```json
        /// {
        ///   "message": "Card deleted successfully",
        ///   "cardId": 2
        /// }
        /// ```
        /// 
        /// **Automatic Default Reassignment:**
        /// If the deleted card was default and other cards exist, 
        /// the most recently added card becomes the new default automatically.
        /// </remarks>
        /// <param name="cardId">ID of the card to delete</param>
        /// <returns>Confirmation message with deleted card ID</returns>
        /// <response code="200">Card successfully deleted</response>
        /// <response code="401">User not authenticated</response>
        /// <response code="404">Card not found or doesn't belong to user</response>
        [Authorize]
        [HttpDelete("cards/{cardId}")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 401)]
        [ProducesResponseType(typeof(object), 404)]
        public async Task<IActionResult> DeleteCard(int cardId)
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var card = await _db.PaymentCards.FirstOrDefaultAsync(c => c.Id == cardId && c.UserId == userId.Value);
            if (card == null) return NotFound("Card not found");

            var wasDefault = card.IsDefault;
            _db.PaymentCards.Remove(card);
            await _db.SaveChangesAsync();

            // If the deleted card was default, set another card as default
            if (wasDefault)
            {
                var newDefault = await _db.PaymentCards
                    .Where(c => c.UserId == userId.Value)
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefaultAsync();

                if (newDefault != null)
                {
                    newDefault.IsDefault = true;
                    await _db.SaveChangesAsync();
                }
            }

            return Ok(new { message = "Card deleted successfully", cardId });
        }

        /// <summary>
        /// Get the default payment card
        /// </summary>
        /// <remarks>
        /// Retrieves only the default payment card for the authenticated user.
        /// This is useful when you need to display or use the primary payment method
        /// without loading all cards.
        /// 
        /// **Response Example:**
        /// ```json
        /// {
        ///   "id": 1,
        ///   "last4": "4242",
        ///   "brand": "Visa",
        ///   "cardholderName": "John Doe",
        ///   "expMonth": 12,
        ///   "expYear": 2025,
        ///   "isDefault": true,
        ///   "createdAt": "2025-01-20T10:30:00Z"
        /// }
        /// ```
        /// 
        /// **Use Cases:**
        /// - Displaying primary payment method on checkout
        /// - Showing default card in user settings
        /// - Quick access to preferred payment method
        /// - Processing automatic payments
        /// 
        /// **Default Card Rules:**
        /// - First card added is automatically default
        /// - Only one card can be default at a time
        /// - Can be changed using PUT /cards/{id}/set-default
        /// - If default card is deleted, most recent card becomes default
        /// </remarks>
        /// <returns>Default card object with full details</returns>
        /// <response code="200">Successfully retrieved default card</response>
        /// <response code="401">User not authenticated</response>
        /// <response code="404">No default card found (user has no cards)</response>
        [Authorize]
        [HttpGet("cards/default")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 401)]
        [ProducesResponseType(typeof(object), 404)]
        public async Task<IActionResult> GetDefaultCard()
        {
            var userId = GetUserIdFromClaims();
            if (!userId.HasValue) return Unauthorized("User ID claim not found in token");

            var card = await _db.PaymentCards
                .Where(c => c.UserId == userId.Value && c.IsDefault)
                .Select(c => new 
                { 
                    id = c.Id, 
                    last4 = c.Last4, 
                    brand = c.Brand,
                    cardholderName = c.CardholderName,
                    expMonth = c.ExpMonth, 
                    expYear = c.ExpYear, 
                    isDefault = c.IsDefault,
                    createdAt = c.CreatedAt
                })
                .FirstOrDefaultAsync();

            if (card == null) return NotFound("No default card found");

            return Ok(card);
        }

        /// <summary>
        /// Detect card brand from card number
        /// </summary>
        private string DetectCardBrand(string cardNumber)
        {
            // Remove spaces and dashes
            var cleaned = new string(cardNumber.Where(char.IsDigit).ToArray());

            if (cleaned.StartsWith("4"))
                return "Visa";
            if (cleaned.StartsWith("5"))
                return "Mastercard";
            if (cleaned.StartsWith("37") || cleaned.StartsWith("34"))
                return "Amex";
            if (cleaned.StartsWith("6"))
                return "Discover";
            
            return "Unknown";
        }
    }
}
