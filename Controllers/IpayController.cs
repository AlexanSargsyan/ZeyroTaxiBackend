using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taxi_API.Data;
using Taxi_API.DTOs;
using Taxi_API.Models;
using Taxi_API.Services;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class IPayController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IPayPaymentService _ipayService;
        private readonly ILogger<IPayController> _logger;

        public IPayController(AppDbContext db, IPayPaymentService ipayService, ILogger<IPayController> logger)
        {
            _db = db;
            _ipayService = ipayService;
            _logger = logger;
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
        /// Create a new IPay payment
        /// </summary>
        [Authorize]
        [HttpPost("create-payment")]
        public async Task<IActionResult> CreatePayment([FromBody] IPayPaymentRequest request)
        {
            if (request == null || request.Amount <= 0)
            {
                return BadRequest("Invalid payment request");
            }

            var userId = GetUserIdFromClaims();
            if (!userId.HasValue)
            {
                return Unauthorized("User ID claim not found in token");
            }

            // Generate unique order number if not provided
            var orderNumber = string.IsNullOrWhiteSpace(request.OrderNumber)
                ? $"IPAY_{DateTime.UtcNow.Ticks}_{userId.Value.ToString("N")[..8]}"
                : request.OrderNumber;

            try
            {
                // Register order with IPay
                var registerResponse = await _ipayService.RegisterOrderAsync(
                    orderNumber,
                    request.Amount,
                    request.Description
                );

                if (registerResponse == null || registerResponse.ErrorCode != 0)
                {
                    _logger.LogError("IPay registration failed: {error}", registerResponse?.ErrorMessage);
                    return StatusCode(502, new { error = registerResponse?.ErrorMessage ?? "IPay service error" });
                }

                // Store payment in database
                var payment = new IPayPayment
                {
                    UserId = userId.Value,
                    OrderNumber = orderNumber,
                    IPayOrderId = registerResponse.OrderId ?? string.Empty,
                    Description = request.Description,
                    Amount = request.Amount,
                    Currency = _ipayService.GetCurrency(),
                    Status = "Created"
                };

                _db.IPayPayments.Add(payment);
                await _db.SaveChangesAsync();

                _logger.LogInformation("Created IPay payment for user {userId}, order {orderNumber}, IPay orderId {orderId}",
                    userId.Value, orderNumber, registerResponse.OrderId);

                return Ok(new IPayPaymentResponse
                {
                    OrderId = registerResponse.OrderId ?? string.Empty,
                    FormUrl = registerResponse.FormUrl ?? string.Empty,
                    Status = "Created",
                    Message = "Payment created successfully. Redirect user to FormUrl to complete payment."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating IPay payment");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get payment status by IPay order ID
        /// </summary>
        [Authorize]
        [HttpGet("status/{orderId}")]
        public async Task<IActionResult> GetPaymentStatus(string orderId)
        {
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return BadRequest("Order ID is required");
            }

            try
            {
                var payment = await _db.IPayPayments
                    .FirstOrDefaultAsync(p => p.IPayOrderId == orderId);

                if (payment == null)
                {
                    return NotFound(new { message = "Payment not found" });
                }

                // Get latest status from IPay
                var statusResponse = await _ipayService.GetOrderStatusAsync(orderId);

                if (statusResponse != null && statusResponse.ErrorCode == 0)
                {
                    // Update payment with latest info
                    payment.Status = GetStatusString(statusResponse.OrderStatus);
                    payment.ActionCode = statusResponse.ActionCode;
                    payment.ActionCodeDescription = statusResponse.ActionCodeDescription;

                    if (statusResponse.CardAuthInfo != null)
                    {
                        payment.Pan = statusResponse.CardAuthInfo.Pan;
                        payment.CardholderName = statusResponse.CardAuthInfo.CardholderName;
                        payment.ApprovalCode = statusResponse.CardAuthInfo.ApprovalCode;
                    }

                    if (statusResponse.OrderStatus == 2) // Deposited
                    {
                        payment.CompletedAt = DateTime.UtcNow;
                    }

                    await _db.SaveChangesAsync();
                }

                _logger.LogInformation("Payment status requested for IPay order {orderId}", orderId);

                return Ok(new
                {
                    orderNumber = payment.OrderNumber,
                    ipayOrderId = payment.IPayOrderId,
                    status = payment.Status,
                    amount = payment.Amount,
                    currency = payment.Currency,
                    description = payment.Description,
                    pan = payment.Pan,
                    cardholderName = payment.CardholderName,
                    approvalCode = payment.ApprovalCode,
                    actionCode = payment.ActionCode,
                    actionCodeDescription = payment.ActionCodeDescription,
                    createdAt = payment.CreatedAt,
                    completedAt = payment.CompletedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting payment status");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Return URL endpoint - called by IPay after payment
        /// </summary>
        [HttpGet("return")]
        [HttpPost("return")]
        public async Task<IActionResult> Return([FromQuery] string? orderId)
        {
            _logger.LogInformation("IPay return callback: orderId={orderId}", orderId);

            if (string.IsNullOrWhiteSpace(orderId))
            {
                return Content(@"
                    <html>
                    <head><title>Payment Result</title></head>
                    <body>
                        <h1>Payment Processing</h1>
                        <p>Unable to determine payment status. Please contact support.</p>
                    </body>
                    </html>
                ", "text/html");
            }

            try
            {
                var payment = await _db.IPayPayments
                    .FirstOrDefaultAsync(p => p.IPayOrderId == orderId);

                if (payment == null)
                {
                    return Content(@"
                        <html>
                        <head><title>Payment Not Found</title></head>
                        <body>
                            <h1>Payment Not Found</h1>
                            <p>Payment record not found.</p>
                        </body>
                        </html>
                    ", "text/html");
                }

                // Get status from IPay
                var statusResponse = await _ipayService.GetOrderStatusAsync(orderId);

                if (statusResponse != null && statusResponse.ErrorCode == 0)
                {
                    payment.Status = GetStatusString(statusResponse.OrderStatus);
                    payment.ActionCode = statusResponse.ActionCode;
                    payment.ActionCodeDescription = statusResponse.ActionCodeDescription;

                    if (statusResponse.CardAuthInfo != null)
                    {
                        payment.Pan = statusResponse.CardAuthInfo.Pan;
                        payment.CardholderName = statusResponse.CardAuthInfo.CardholderName;
                        payment.ApprovalCode = statusResponse.CardAuthInfo.ApprovalCode;
                    }

                    if (statusResponse.OrderStatus == 2) // Deposited
                    {
                        payment.CompletedAt = DateTime.UtcNow;
                    }

                    await _db.SaveChangesAsync();

                    var isSuccess = statusResponse.OrderStatus == 2; // Deposited
                    var title = isSuccess ? "Payment Successful" : "Payment Failed";
                    var message = isSuccess 
                        ? "Your payment has been processed successfully."
                        : $"Payment failed: {statusResponse.ActionCodeDescription}";

                    return Content($@"
                        <html>
                        <head><title>{title}</title></head>
                        <body>
                            <h1>{title}</h1>
                            <p>{message}</p>
                            <p>Order Number: {payment.OrderNumber}</p>
                            <p>Amount: {payment.Amount} AMD</p>
                        </body>
                        </html>
                    ", "text/html");
                }

                return Content(@"
                    <html>
                    <head><title>Payment Processing</title></head>
                    <body>
                        <h1>Payment Processing</h1>
                        <p>Unable to verify payment status. Please check your order history.</p>
                    </body>
                    </html>
                ", "text/html");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing return callback");
                return Content(@"
                    <html>
                    <head><title>Error</title></head>
                    <body>
                        <h1>Error</h1>
                        <p>An error occurred while processing your payment. Please contact support.</p>
                    </body>
                    </html>
                ", "text/html");
            }
        }

        /// <summary>
        /// Reverse (cancel) a payment
        /// </summary>
        [Authorize]
        [HttpPost("reverse/{orderId}")]
        public async Task<IActionResult> ReversePayment(string orderId)
        {
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return BadRequest("Order ID is required");
            }

            try
            {
                var payment = await _db.IPayPayments
                    .FirstOrDefaultAsync(p => p.IPayOrderId == orderId);

                if (payment == null)
                {
                    return NotFound(new { message = "Payment not found" });
                }

                var response = await _ipayService.ReversePaymentAsync(orderId);

                if (response != null && response.ErrorCode == 0)
                {
                    payment.Status = "Reversed";
                    await _db.SaveChangesAsync();

                    _logger.LogInformation("Payment reversed: {orderId}", orderId);
                    return Ok(new { message = "Payment reversed successfully" });
                }

                return StatusCode(502, new { error = response?.ErrorMessage ?? "Failed to reverse payment" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reversing payment");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Refund a payment
        /// </summary>
        [Authorize]
        [HttpPost("refund")]
        public async Task<IActionResult> RefundPayment([FromBody] IPayRefundRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.OrderId) || request.Amount <= 0)
            {
                return BadRequest("Invalid refund request");
            }

            try
            {
                var payment = await _db.IPayPayments
                    .FirstOrDefaultAsync(p => p.IPayOrderId == request.OrderId);

                if (payment == null)
                {
                    return NotFound(new { message = "Payment not found" });
                }

                if (request.Amount > payment.Amount)
                {
                    return BadRequest("Refund amount cannot exceed payment amount");
                }

                var response = await _ipayService.RefundPaymentAsync(request.OrderId, request.Amount);

                if (response != null && response.ErrorCode == 0)
                {
                    payment.Status = "Refunded";
                    await _db.SaveChangesAsync();

                    _logger.LogInformation("Payment refunded: {orderId}, amount: {amount}", request.OrderId, request.Amount);
                    return Ok(new { message = "Payment refunded successfully" });
                }

                return StatusCode(502, new { error = response?.ErrorMessage ?? "Failed to refund payment" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refunding payment");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        private string GetStatusString(int orderStatus)
        {
            return orderStatus switch
            {
                0 => "Created",
                1 => "Approved",
                2 => "Deposited",
                3 => "Reversed",
                4 => "Refunded",
                5 => "ACS_Auth",
                6 => "Declined",
                _ => "Unknown"
            };
        }
    }
}
