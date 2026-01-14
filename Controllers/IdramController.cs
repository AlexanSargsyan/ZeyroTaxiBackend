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
    public class IdramController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IdramPaymentService _idramService;
        private readonly ILogger<IdramController> _logger;

        public IdramController(AppDbContext db, IdramPaymentService idramService, ILogger<IdramController> logger)
        {
            _db = db;
            _idramService = idramService;
            _logger = logger;
        }

        /// <summary>
        /// Generate Idram payment form data for client to submit
        /// </summary>
        [Authorize]
        [HttpPost("create-payment")]
        public async Task<IActionResult> CreatePayment([FromBody] IdramPaymentRequest request)
        {
            if (request == null || request.Amount <= 0)
            {
                return BadRequest("Invalid payment request");
            }

            var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (!Guid.TryParse(userIdStr, out var userId))
            {
                return Unauthorized();
            }

            // Generate a unique bill number if not provided
            var billNo = string.IsNullOrWhiteSpace(request.BillNo) 
                ? $"BILL_{DateTime.UtcNow.Ticks}_{userId.ToString("N")[..8]}" 
                : request.BillNo;

            // Store payment intent in database
            var payment = new IdramPayment
            {
                UserId = userId,
                BillNo = billNo,
                Description = request.Description,
                Amount = request.Amount,
                Status = "Pending"
            };

            _db.IdramPayments.Add(payment);
            await _db.SaveChangesAsync();

            var formData = new IdramPaymentFormData
            {
                PaymentUrl = _idramService.GetPaymentUrl(),
                FormFields = new Dictionary<string, string>
                {
                    { "EDP_LANGUAGE", request.Language },
                    { "EDP_REC_ACCOUNT", _idramService.GetReceiverAccount() },
                    { "EDP_DESCRIPTION", request.Description },
                    { "EDP_AMOUNT", request.Amount.ToString("F2") },
                    { "EDP_BILL_NO", billNo }
                }
            };

            // Add email if provided
            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                formData.FormFields["EDP_EMAIL"] = request.Email;
            }
            else if (!string.IsNullOrWhiteSpace(_idramService.GetEmail()))
            {
                formData.FormFields["EDP_EMAIL"] = _idramService.GetEmail();
            }

            _logger.LogInformation("Created Idram payment for user {userId}, bill {billNo}, amount {amount}",
                userId, billNo, request.Amount);

            return Ok(formData);
        }

        /// <summary>
        /// Idram preliminary check endpoint (order authenticity confirmation)
        /// </summary>
        [HttpPost("result")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> HandleResult()
        {
            // Read form data manually to avoid duplicate key issues in Swagger
            var form = await Request.ReadFormAsync();
            
            var precheck = form["EDP_PRECHECK"].ToString();
            var billNo = form["EDP_BILL_NO"].ToString();
            var recAccount = form["EDP_REC_ACCOUNT"].ToString();
            var amount = form["EDP_AMOUNT"].ToString();

            // Check if this is a preliminary check
            if (!string.IsNullOrWhiteSpace(precheck) && 
                precheck.Equals("YES", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Idram precheck: billNo={billNo}, amount={amount}, recAccount={recAccount}",
                    billNo, amount, recAccount);

                // Validate order authenticity
                var isValid = _idramService.ValidateOrderPrecheck(billNo, recAccount, amount);

                if (!isValid)
                {
                    _logger.LogWarning("Idram precheck failed for bill {billNo}", billNo);
                    return Content("FAIL");
                }

                // Check if payment exists in database
                var payment = await _db.IdramPayments
                    .FirstOrDefaultAsync(p => p.BillNo == billNo);

                if (payment == null)
                {
                    _logger.LogWarning("Payment not found for bill {billNo}", billNo);
                    return Content("FAIL");
                }

                // Validate amount matches
                if (decimal.TryParse(amount, out var parsedAmount) && parsedAmount != payment.Amount)
                {
                    _logger.LogWarning("Amount mismatch for bill {billNo}. Expected: {expected}, Got: {actual}",
                        billNo, payment.Amount, amount);
                    return Content("FAIL");
                }

                _logger.LogInformation("Idram precheck successful for bill {billNo}", billNo);
                return Content("OK");
            }

            // Payment confirmation
            var transId = form["EDP_TRANS_ID"].ToString();
            
            if (!string.IsNullOrWhiteSpace(transId))
            {
                var payerAccount = form["EDP_PAYER_ACCOUNT"].ToString();
                var transDate = form["EDP_TRANS_DATE"].ToString();
                var checksum = form["EDP_CHECKSUM"].ToString();

                _logger.LogInformation("Idram payment confirmation: transId={transId}, billNo={billNo}, amount={amount}",
                    transId, billNo, amount);

                // Validate checksum
                var isValid = _idramService.ValidateChecksum(
                    recAccount,
                    amount,
                    billNo,
                    payerAccount,
                    transId,
                    transDate,
                    checksum);

                if (!isValid)
                {
                    _logger.LogError("Idram checksum validation failed for transaction {transId}", transId);
                    return Content("FAIL");
                }

                // Process the successful payment
                try
                {
                    var payment = await _db.IdramPayments
                        .FirstOrDefaultAsync(p => p.BillNo == billNo);

                    if (payment == null)
                    {
                        _logger.LogError("Payment not found for bill {billNo}", billNo);
                        return Content("FAIL");
                    }

                    // Update payment record
                    payment.Status = "Success";
                    payment.PayerAccount = payerAccount;
                    payment.TransactionId = transId;
                    payment.TransactionDate = transDate;
                    payment.CompletedAt = DateTime.UtcNow;

                    await _db.SaveChangesAsync();

                    _logger.LogInformation("Idram payment processed successfully: {transId}", transId);
                    return Content("OK");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Idram payment confirmation");
                    return Content("FAIL");
                }
            }

            _logger.LogWarning("Invalid Idram callback received");
            return BadRequest("Invalid request");
        }

        /// <summary>
        /// Success page redirect endpoint
        /// </summary>
        [HttpGet("success")]
        [HttpPost("success")]
        public IActionResult Success([FromQuery] string? billNo)
        {
            _logger.LogInformation("Idram payment success redirect: billNo={billNo}", billNo);
            
            // Return HTML page or redirect to your frontend
            return Content(@"
                <html>
                <head><title>Payment Successful</title></head>
                <body>
                    <h1>Payment Successful</h1>
                    <p>Your payment has been processed successfully.</p>
                    <p>Bill Number: " + billNo + @"</p>
                </body>
                </html>
            ", "text/html");
        }

        /// <summary>
        /// Failure page redirect endpoint
        /// </summary>
        [HttpGet("fail")]
        [HttpPost("fail")]
        public IActionResult Fail([FromQuery] string? billNo)
        {
            _logger.LogInformation("Idram payment failed redirect: billNo={billNo}", billNo);
            
            // Return HTML page or redirect to your frontend
            return Content(@"
                <html>
                <head><title>Payment Failed</title></head>
                <body>
                    <h1>Payment Failed</h1>
                    <p>Your payment could not be processed.</p>
                    <p>Bill Number: " + billNo + @"</p>
                </body>
                </html>
            ", "text/html");
        }

        /// <summary>
        /// Get payment status by bill number
        /// </summary>
        [Authorize]
        [HttpGet("status/{billNo}")]
        public async Task<IActionResult> GetPaymentStatus(string billNo)
        {
            if (string.IsNullOrWhiteSpace(billNo))
            {
                return BadRequest("Bill number is required");
            }

            var payment = await _db.IdramPayments
                .FirstOrDefaultAsync(p => p.BillNo == billNo);

            if (payment == null)
            {
                return NotFound(new { message = "Payment not found" });
            }

            _logger.LogInformation("Payment status requested for bill {billNo}", billNo);
            
            return Ok(new 
            { 
                billNo = payment.BillNo, 
                status = payment.Status, 
                amount = payment.Amount,
                description = payment.Description,
                transactionId = payment.TransactionId,
                transactionDate = payment.TransactionDate,
                createdAt = payment.CreatedAt,
                completedAt = payment.CompletedAt
            });
        }
    }
}
