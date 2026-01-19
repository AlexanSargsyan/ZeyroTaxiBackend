# Voice AI - Input/Output Mode Guide

## ?? Overview

The Voice AI system automatically adapts its response format based on the **input type**:

- ?? **Voice Input** (audio file) ? ?? **Voice Output** (audio response)
- ?? **Text Input** (text message) ? ?? **Text Output** (JSON response)

This creates a natural, intuitive user experience where the AI responds in the same mode as the input.

---

## ?? Behavior Summary

| Endpoint | Input Type | Output Type | Use Case |
|----------|------------|-------------|----------|
| `POST /api/voice/upload` | Voice (file) | Voice (audio) | Voice conversations |
| `POST /api/voice/chat` | Text (JSON) | Text (JSON) | Text chat interface |
| `POST /api/voice/translate` | Text (JSON) | Text or Voice | Translation with optional TTS |

---

## 1?? Voice Input ? Voice Output

### Endpoint: `POST /api/voice/upload`

**Behavior:** Upload audio file, get audio response automatically.

#### Parameters:
- `file` (IFormFile) - Audio file (WAV, MP3, etc.)
- `lang` (string, optional) - Language code: `en`, `ru`, `hy` (default: `en`)
- `voice` (string, optional) - Voice ID: `alloy`, `nova`, `shimmer`, etc.

#### Request Example (cURL):
```bash
curl -X POST "http://localhost:5000/api/voice/upload" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@my_voice_message.wav" \
  -F "lang=ru" \
  -F "voice=nova"
```

#### Response:
- **Content-Type:** `audio/wav`
- **Body:** Audio file (WAV format)
- **Headers:**
  - `X-Transcription`: Original transcribed text
  - `X-Intent`: Detected intent (chat/taxi/delivery/schedule)
  - `X-Language`: Language used
  - `X-Order-Created`: `true` (if order was created)
  - `X-Order-Id`: Order GUID (if created)

---

## 2?? Text Input ? Text Output

### Endpoint: `POST /api/voice/chat`

**Behavior:** Send text message, get JSON response with text reply.

#### Request Body:
```json
{
  "text": "I need a taxi to the airport",
  "lang": "en",
  "voice": "nova"
}
```

#### Response:
```json
{
  "text": "I need a taxi to the airport",
  "intent": "taxi",
  "reply": "Sure! I'll help you book a taxi to the airport. Where should we pick you up?",
  "order": {
    "id": "guid",
    "action": "taxi",
    "destination": "airport"
  },
  "language": "en"
}
```

---

## ? Summary

### Voice Input (POST /api/voice/upload):
- ?? **Input:** Audio file
- ?? **Output:** Audio response (automatic)
- ?? **Metadata:** HTTP headers

### Text Input (POST /api/voice/chat):
- ?? **Input:** JSON text
- ?? **Output:** JSON response (automatic)
- ?? **Metadata:** JSON fields

### Key Benefits:
? **Natural UX** - Input type determines output type  
? **Automatic** - No need to specify audio parameter  
? **Consistent** - Same behavior across all languages  

---

**Last Updated:** January 2026  
**Status:** ? Production Ready
