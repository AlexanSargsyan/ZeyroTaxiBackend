using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taxi_API.Services;
using Taxi_API.Data;
using Taxi_API.Models;
using System.Text.Json;

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

        [HttpPost("upload")]
        [Authorize]
        public async Task<IActionResult> UploadVoice([FromForm] IFormFile file, [FromForm] string? lang, [FromForm] bool audio = false)
        {
            if (file == null || file.Length == 0) return BadRequest("No audio file provided");

            // default to English if not provided
            lang ??= "en";

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;

            var text = await _openAi.TranscribeAsync(ms, lang);
            if (text == null) return StatusCode(502, "Transcription failed");

            // Keyword detection across English, Armenian (hy) and Russian (ru)
            var lower = text.ToLowerInvariant();
            string intent = "chat";

            var taxiKeywords = new[] { "taxi", "?????", "?????" };
            var deliveryKeywords = new[] { "delivery", "???????", "????????" };
            var scheduleKeywords = new[] { "schedule", "???", "???????", "??????????" };

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

            if (audio)
            {
                var audioBytes = await _openAi.SynthesizeSpeechAsync(reply, lang);
                if (audioBytes == null) return StatusCode(502, "TTS failed");
                return File(audioBytes, "audio/wav", "reply.wav");
            }

            return Ok(new { transcription = text, intent, reply, order = created });
        }

        // New translate endpoint
        public record TranslateRequest(string Text, string To, string? From = null, bool Audio = false, string? Voice = null);

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
                    "hy" or "armenian" or "arm" => "hy",
                    "ru" or "rus" or "russian" => "ru",
                    "en" or "eng" or "english" => "en",
                    _ => string.Empty
                };
            }

            var from = string.IsNullOrWhiteSpace(req.From) ? "auto" : NormalizeLang(req.From) ?? "auto";
            var to = NormalizeLang(req.To);
            if (string.IsNullOrEmpty(to)) return BadRequest("Unsupported target language. Supported: hy, ru, en");

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

            return Ok(new { text = req.Text, translation });
        }

        // New text chat endpoint for chat page
        public record ChatRequest(string Text, string? Lang = null, bool Audio = false);

        [HttpPost("chat")]
        [Authorize]
        public async Task<IActionResult> Chat([FromBody] ChatRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Text)) return BadRequest("Text is required");

            var lang = string.IsNullOrWhiteSpace(req.Lang) ? "en" : req.Lang;
            var text = req.Text.Trim();

            // Keyword detection across supported languages
            var lower = text.ToLowerInvariant();
            string intent = "chat";

            var taxiKeywords = new[] { "taxi", "պտի՞", "такси" };
            var deliveryKeywords = new[] { "delivery", "բերել", "доставка" };
            var scheduleKeywords = new[] { "schedule", "ժամ", "расписание" };

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

            if (req.Audio)
            {
                var audioBytes = await _openAi.SynthesizeSpeechAsync(reply, lang);
                if (audioBytes == null) return StatusCode(502, "TTS failed");
                return File(audioBytes, "audio/wav", "reply.wav");
            }

            return Ok(new { text, intent, reply, order = created });
        }
    }
}
