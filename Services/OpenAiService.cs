using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Taxi_API.Services
{
    // Minimal OpenAI service using REST API to transcribe audio and chat, and synthesize speech.
    // Configuration: add "OpenAI:ApiKey" in appsettings or environment variables.
    public class OpenAiService : IOpenAiService
    {
        private readonly string _apiKey;
        private readonly HttpClient _http;

        public OpenAiService(IConfiguration config)
        {
            _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
            _http = new HttpClient();
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        public async Task<string?> TranscribeAsync(Stream audioStream, string language)
        {
            var url = "https://api.openai.com/v1/audio/transcriptions";
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(audioStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(streamContent, "file", "audio.wav");
            content.Add(new StringContent("whisper-1"), "model");
            if (!string.IsNullOrEmpty(language))
            {
                // OpenAI expects ISO language codes like "en", "ru", "hy" for Armenian
                content.Add(new StringContent(language), "language");
            }

            var res = await _http.PostAsync(url, content);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var text))
            {
                return text.GetString();
            }
            return null;
        }

        public async Task<string?> ChatAsync(string prompt, string language)
        {
            var url = "https://api.openai.com/v1/chat/completions";

            // Map short language code to human-readable name for instructions
            var langName = language switch
            {
                "hy" => "Armenian",
                "ru" => "Russian",
                "en" => "English",
                _ => language
            };

            var system = $"You are an assistant for a taxi service. Recognize keywords: taxi, delivery, schedule. Always reply concisely in {langName}. If user intent is taxi/delivery/schedule return a short JSON object with fields 'action' and 'details' and then a brief human-readable sentence in the same language.";

            var messages = new[] {
                new { role = "system", content = system },
                new { role = "user", content = prompt }
            };

            var payload = new
            {
                model = "gpt-4o-mini",
                messages,
                max_tokens = 300,
                temperature = 0.2
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var res = await _http.PostAsync(url, content);
            if (!res.IsSuccessStatusCode) return null;
            var resJson = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(resJson);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message").GetProperty("content").GetString();
                return message;
            }
            return null;
        }

        public async Task<byte[]?> SynthesizeSpeechAsync(string text, string language, string? voice = null)
        {
            // Use confirmed OpenAI TTS endpoint path
            var url = "https://api.openai.com/v1/audio/speech";

            // Choose a default voice per language (can be customized)
            var selectedVoice = voice ?? (language switch
            {
                "hy" => "alloy", // fallback; replace with specific Armenian voice if available
                "ru" => "alloy",
                "en" => "alloy",
                _ => "alloy"
            });

            var payload = new
            {
                model = "gpt-4o-mini-tts",
                voice = selectedVoice,
                input = text,
                language = language,
                format = "wav"
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Request WAV audio
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));

            var res = await _http.SendAsync(request);
            if (!res.IsSuccessStatusCode) return null;

            var bytes = await res.Content.ReadAsByteArrayAsync();
            return bytes;
        }
    }
}
