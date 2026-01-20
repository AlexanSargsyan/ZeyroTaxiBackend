using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taxi_API.Services;
using Taxi_API.Data;
using Taxi_API.Models;
using System.Text.Json;
using Swashbuckle.AspNetCore.Annotations;

namespace Taxi_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class VoiceController : ControllerBase
    {
        private readonly IOpenAiService _openAi;
        private readonly AppDbContext _db;

        public VoiceController(IOpenAiService openAi, AppDbContext db)
        {
            _openAi = openAi;
            _db = db;
        }

        public class VoiceUploadRequest
        {
            public IFormFile File { get; set; } = null!;
            public string Lang { get; set; } = "en";
        }

        /// <summary>
        /// Upload an audio file for voice-to-text transcription with AI response
        /// </summary>
        /// <param name="request">Voice upload request containing audio file and language</param>
        /// <returns>Returns audio file (WAV) with AI response. Check response headers for transcription and intent details</returns>
        /// <response code="200">Returns WAV audio file with AI voice response</response>
        /// <response code="400">No audio file provided or unsupported language</response>
        /// <response code="401">Unauthorized - JWT token required</response>
        /// <response code="502">Transcription, chat, or TTS service failed</response>
        /// <remarks>
        /// **Simple usage:** Just upload the audio file. The `lang` parameter is optional and defaults to "en".
        /// 
        /// **Supported audio formats:**
        /// - WAV (.wav)
        /// - MP3 (.mp3)
        /// - M4A (.m4a)
        /// - OGG (.ogg)
        /// 
        /// **Response headers contain:**
        /// - `X-Transcription`: Your spoken text (transcribed)
        /// - `X-Intent`: Detected intent (chat, taxi, delivery, or schedule)
        /// - `X-Language`: Language used
        /// - `X-Order-Created`: "true" if an order was automatically created
        /// - `X-Order-Id`: Order GUID if created
        /// 
        /// **Example:**
        /// 
        /// Minimal request (most common):
        /// ```
        /// POST /api/voice/upload
        /// Content-Type: multipart/form-data
        /// Authorization: Bearer YOUR_TOKEN
        /// 
        /// File: [your_audio.wav]
        /// Lang: "en"
        /// ```
        /// 
        /// With language specified:
        /// ```
        /// POST /api/voice/upload
        /// Content-Type: multipart/form-data
        /// Authorization: Bearer YOUR_TOKEN
        /// 
        /// File: [your_audio.wav]
        /// Lang: "ru"
        /// ```
        /// </remarks>
        [HttpPost("upload")]
        [Authorize]
        [Consumes("multipart/form-data")]
        [SwaggerOperation(
            Summary = "Upload audio file for AI voice transcription and response",
            Description = "Upload an audio file (wav, mp3, m4a, ogg) to transcribe and get AI voice response"
        )]
        [ProducesResponseType(typeof(FileResult), 200, "audio/wav")]
        public async Task<IActionResult> UploadVoice([FromForm] VoiceUploadRequest request)
        {
            if (request.File == null || request.File.Length == 0) return BadRequest("No audio file provided");

            var lang = string.IsNullOrWhiteSpace(request.Lang) ? "en" : request.Lang;

            // Validate language
            var supportedLanguages = new[] { "en", "ru", "hy" };
            if (!supportedLanguages.Contains(lang.ToLower()))
            {
                return BadRequest($"Unsupported language '{lang}'. Supported: en (English), ru (Russian), hy (Armenian)");
            }

            using var ms = new MemoryStream();
            await request.File.CopyToAsync(ms);
            ms.Position = 0;

            var text = await _openAi.TranscribeAsync(ms, lang);
            if (text == null) return StatusCode(502, "Transcription failed");

            // Keyword detection across English, Armenian (hy) and Russian (ru)
            var lower = text.ToLowerInvariant();
            string intent = "chat";

            var taxiKeywords = new[] { "taxi", "տաքսի", "такси" };
            var deliveryKeywords = new[] { "delivery", "առաքում", "доставка" };
            var scheduleKeywords = new[] { "schedule", "ժամ", "график", "расписание" };

            if (taxiKeywords.Any(k => lower.Contains(k))) intent = "taxi";
            else if (deliveryKeywords.Any(k => lower.Contains(k))) intent = "delivery";
            else if (scheduleKeywords.Any(k => lower.Contains(k))) intent = "schedule";

            // Build prompt for chat model
            var prompt = $"User said (in {lang}): \n\"{text}\"\n\nDetected intent: {intent}.\nRespond in the same language concisely and if intent is taxi/delivery/schedule produce a short JSON with action and details.";

            var reply = await _openAi.ChatAsync(prompt, lang);
            if (reply == null) return StatusCode(502, "Chat failed");

            Order? created = null;

            if (intent == "taxi" || intent == "delivery" || intent == "schedule")
            {
                // Try extract JSON block from reply
                var jsonStart = reply.IndexOf('{');
                var jsonEnd = reply.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonStr = reply.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    try
                    {
                        using var doc = JsonDocument.Parse(jsonStr);
                        var root = doc.RootElement;
                        var order = new Order();
                        order.Action = root.GetProperty("action").GetString() ?? intent;
                        if (root.TryGetProperty("pickup", out var pu)) order.Pickup = pu.GetString();
                        if (root.TryGetProperty("destination", out var de)) order.Destination = de.GetString();
                        if (root.TryGetProperty("packageDetails", out var pd)) order.PackageDetails = pd.GetString();
                        if (root.TryGetProperty("scheduledFor", out var sf) && sf.ValueKind == JsonValueKind.String)
                        {
                            if (DateTime.TryParse(sf.GetString(), out var dt)) order.ScheduledFor = dt;
                        }

                        // Associate with current user
                        var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
                        if (Guid.TryParse(userIdStr, out var userId))
                        {
                            order.UserId = userId;
                            order.CreatedAt = DateTime.UtcNow;
                            _db.Orders.Add(order);
                            await _db.SaveChangesAsync();
                            created = order;
                        }
                    }
                    catch
                    {
                        // ignore parsing errors
                    }
                }
            }

            // Voice input ALWAYS gets voice output (using default voice)
            var audioBytes = await _openAi.SynthesizeSpeechAsync(reply, lang, null);
            if (audioBytes == null) return StatusCode(502, "TTS failed");
            
            // Return audio file with metadata in headers
            Response.Headers.Add("X-Transcription", text);
            Response.Headers.Add("X-Intent", intent);
            Response.Headers.Add("X-Language", lang);
            if (created != null)
            {
                Response.Headers.Add("X-Order-Created", "true");
                Response.Headers.Add("X-Order-Id", created.Id.ToString());
            }
            
            return File(audioBytes, "audio/wav", "reply.wav");
        }

        // New translate endpoint
        public record TranslateRequest(string Text, string To, string? From = null, bool Audio = false, string? Voice = null);

        /// <summary>
        /// Translate text between supported languages
        /// </summary>
        /// <param name="req">Translation request containing text and target language</param>
        /// <returns>Returns translated text as JSON, or audio file if Audio=true</returns>
        /// <response code="200">Returns translation (JSON or audio file)</response>
        /// <response code="400">Invalid request or unsupported language</response>
        /// <response code="401">Unauthorized - JWT token required</response>
        /// <response code="502">Translation or TTS service failed</response>
        [HttpPost("translate")]
        [Authorize]
        public async Task<IActionResult> Translate([FromBody] TranslateRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Text) || string.IsNullOrWhiteSpace(req.To))
                return BadRequest("Text and To language are required");

            // Normalize language codes and allow common synonyms
            string NormalizeLang(string l)
            {
                if (string.IsNullOrWhiteSpace(l)) return string.Empty;
                var s = l.Trim().ToLowerInvariant();
                return s switch
                {
                    "hy" or "armenian" or "arm" or "հայերեն" => "hy",
                    "ru" or "rus" or "russian" or "русский" => "ru",
                    "en" or "eng" or "english" => "en",
                    _ => string.Empty
                };
            }

            var from = string.IsNullOrWhiteSpace(req.From) ? "auto" : NormalizeLang(req.From) ?? "auto";
            var to = NormalizeLang(req.To);
            if (string.IsNullOrEmpty(to)) return BadRequest("Unsupported target language. Supported: hy (Armenian), ru (Russian), en (English)");

            // Build prompt for translation
            var prompt = from == "auto"
                ? $"Translate the following text to {to} concisely, preserve meaning and do not add commentary. Text:\n{req.Text}"
                : $"Translate the following text from {from} to {to} concisely, preserve meaning and do not add commentary. Text:\n{req.Text}";

            var translation = await _openAi.ChatAsync(prompt, to);
            if (translation == null) return StatusCode(502, "Translation failed");

            if (req.Audio)
            {
                // synthesize TTS in target language
                var audioBytes = await _openAi.SynthesizeSpeechAsync(translation, to, req.Voice);
                if (audioBytes == null) return StatusCode(502, "TTS failed");
                return File(audioBytes, "audio/wav", "translation.wav");
            }

            return Ok(new { text = req.Text, translation, from = from, to = to });
        }

        // Text chat endpoint - text input gets text output
        public record ChatRequest(string Text, string? Lang = null, string? Voice = null);

        /// <summary>
        /// Send text message to AI chat assistant
        /// </summary>
        /// <param name="req">Chat request - can be either plain text string or JSON object with text and optional language</param>
        /// <returns>Returns JSON response with AI reply and detected intent. Can create orders for taxi/delivery/schedule intents</returns>
        /// <response code="200">Returns chat response with AI reply and metadata</response>
        /// <response code="400">Invalid request or missing text</response>
        /// <response code="401">Unauthorized - JWT token required</response>
        /// <response code="502">Chat service failed</response>
        /// <remarks>
        /// **Flexible input format - accepts both:**
        /// 
        /// **Option 1: Plain text string (simple)**
        /// ```json
        /// "Hello, I need a taxi"
        /// ```
        /// 
        /// **Option 2: JSON object (with language control)**
        /// ```json
        /// {
        ///   "text": "Hello, I need a taxi",
        ///   "lang": "en"
        /// }
        /// ```
        /// 
        /// Both formats work identically. Use plain text for simplicity or JSON object for language control.
        /// </remarks>
        [HttpPost("chat")]
        [Authorize]
        public async Task<IActionResult> Chat([FromBody] object input)
        {
            // Parse input - accept both plain string and ChatRequest object
            string text;
            string lang = "en";

            if (input is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    // Plain string input
                    text = jsonElement.GetString() ?? string.Empty;
                }
                else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // JSON object input
                    if (jsonElement.TryGetProperty("text", out var textProp))
                    {
                        text = textProp.GetString() ?? string.Empty;
                    }
                    else if (jsonElement.TryGetProperty("Text", out var textPropCapital))
                    {
                        text = textPropCapital.GetString() ?? string.Empty;
                    }
                    else
                    {
                        return BadRequest("Text property is required in the request object");
                    }

                    if (jsonElement.TryGetProperty("lang", out var langProp))
                    {
                        lang = langProp.GetString() ?? "en";
                    }
                    else if (jsonElement.TryGetProperty("Lang", out var langPropCapital))
                    {
                        lang = langPropCapital.GetString() ?? "en";
                    }
                }
                else
                {
                    return BadRequest("Invalid input format. Expected plain text string or JSON object with 'text' property");
                }
            }
            else
            {
                return BadRequest("Invalid input format");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return BadRequest("Text is required and cannot be empty");
            }

            // Validate language
            var supportedLanguages = new[] { "en", "ru", "hy" };
            lang = lang.ToLower();
            if (!supportedLanguages.Contains(lang))
            {
                return BadRequest($"Unsupported language '{lang}'. Supported: en (English), ru (Russian), hy (Armenian)");
            }

            text = text.Trim();

            // Keyword detection across supported languages
            var lower = text.ToLowerInvariant();
            string intent = "chat";

            var taxiKeywords = new[] { "taxi", "տաքսի", "такси" };
            var deliveryKeywords = new[] { "delivery", "առաքում", "доставка" };
            var scheduleKeywords = new[] { "schedule", "ժամ", "график", "расписание" };

            if (taxiKeywords.Any(k => lower.Contains(k))) intent = "taxi";
            else if (deliveryKeywords.Any(k => lower.Contains(k))) intent = "delivery";
            else if (scheduleKeywords.Any(k => lower.Contains(k))) intent = "schedule";

            var prompt = $"User said (in {lang}): \n\"{text}\"\n\nDetected intent: {intent}.\nRespond in the same language concisely and if intent is taxi/delivery/schedule produce a short JSON with action and details.";

            var reply = await _openAi.ChatAsync(prompt, lang);
            if (reply == null) return StatusCode(502, "Chat failed");

            Order? created = null;

            if (intent == "taxi" || intent == "delivery" || intent == "schedule")
            {
                var jsonStart = reply.IndexOf('{');
                var jsonEnd = reply.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var jsonStr = reply.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    try
                    {
                        using var doc = JsonDocument.Parse(jsonStr);
                        var root = doc.RootElement;
                        var order = new Order();
                        order.Action = root.GetProperty("action").GetString() ?? intent;
                        if (root.TryGetProperty("pickup", out var pu)) order.Pickup = pu.GetString();
                        if (root.TryGetProperty("destination", out var de)) order.Destination = de.GetString();
                        if (root.TryGetProperty("packageDetails", out var pd)) order.PackageDetails = pd.GetString();
                        if (root.TryGetProperty("scheduledFor", out var sf) && sf.ValueKind == JsonValueKind.String)
                        {
                            if (DateTime.TryParse(sf.GetString(), out var dt)) order.ScheduledFor = dt;
                        }

                        // Associate with current user if available
                        var userIdStr = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
                        if (Guid.TryParse(userIdStr, out var userId))
                        {
                            order.UserId = userId;
                            order.CreatedAt = DateTime.UtcNow;
                            _db.Orders.Add(order);
                            await _db.SaveChangesAsync();
                            created = order;
                        }
                    }
                    catch
                    {
                        // ignore parsing errors
                    }
                }
            }

            // Text input ALWAYS gets text output (JSON)
            return Ok(new { text, intent, reply, order = created, language = lang });
        }
    }
}
