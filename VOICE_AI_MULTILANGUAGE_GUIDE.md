# Voice AI & Multi-Language Support Guide

## Overview

The Zeyro Taxi API supports AI-powered voice and chat features in **three languages**:
- ???? **English (en)**
- ???? **Russian (ru)**
- ???? **Armenian (hy)**

All endpoints support:
- Voice file upload (audio transcription)
- Text-to-Speech (TTS) responses
- Custom voice selection
- Intent detection (taxi, delivery, schedule)

---

## Supported Languages

| Language | Code | Native Name | Voice Support |
|----------|------|-------------|---------------|
| English | `en` | English | ? Yes |
| Russian | `ru` | ??????? | ? Yes |
| Armenian | `hy` | ??????? | ? Yes |

---

## API Endpoints

### 1. Voice Upload & Transcription
**Endpoint:** `POST /api/voice/upload`

Upload an audio file for transcription, AI processing, and optional TTS response.

#### Parameters:
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `file` | IFormFile | Yes | Audio file (WAV, MP3, etc.) |
| `lang` | string | No | Language code (en/ru/hy). Default: `en` |
| `audio` | boolean | No | Return TTS audio response. Default: `false` |
| `voice` | string | No | Voice ID for TTS (e.g., `alloy`, `echo`, `fable`, `onyx`, `nova`, `shimmer`) |

#### Request (Swagger):
```
POST /api/voice/upload
Authorization: Bearer {token}
Content-Type: multipart/form-data

file: [Select audio file]
lang: ru
audio: true
voice: alloy
```

#### Request (cURL):
```bash
curl -X POST "http://localhost:5000/api/voice/upload" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@audio.wav" \
  -F "lang=ru" \
  -F "audio=true" \
  -F "voice=alloy"
```

#### Response (Text):
```json
{
  "transcription": "??? ????? ????? ? ????????",
  "intent": "taxi",
  "reply": "?????, ?? ?????? ???????? ????? ? ????????. {\"action\":\"taxi\",\"destination\":\"????????\"}",
  "order": {
    "id": "guid",
    "action": "taxi",
    "destination": "????????",
    "createdAt": "2026-01-16T10:00:00Z"
  },
  "language": "ru"
}
```

#### Response (Audio):
When `audio=true`, returns WAV audio file with TTS response.

---

### 2. Text Chat
**Endpoint:** `POST /api/voice/chat`

Text-based chat with AI assistant, with optional TTS response.

#### Request Body:
```json
{
  "text": "I need a taxi to the airport",
  "lang": "en",
  "audio": true,
  "voice": "nova"
}
```

#### Parameters:
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `text` | string | Yes | User message |
| `lang` | string | No | Language code. Default: `en` |
| `audio` | boolean | No | Return TTS audio. Default: `false` |
| `voice` | string | No | Voice ID for TTS |

#### Request (cURL):
```bash
curl -X POST "http://localhost:5000/api/voice/chat" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "??? ????? ?????",
    "lang": "ru",
    "audio": false,
    "voice": "alloy"
  }'
```

#### Response (Text):
```json
{
  "text": "??? ????? ?????",
  "intent": "taxi",
  "reply": "??????, ? ?????? ??? ???????? ?????. ???? ?? ?????? ????????",
  "order": null,
  "language": "ru"
}
```

---

### 3. Translation
**Endpoint:** `POST /api/voice/translate`

Translate text between supported languages with optional TTS.

#### Request Body:
```json
{
  "text": "I need a taxi to the airport",
  "from": "en",
  "to": "ru",
  "audio": true,
  "voice": "alloy"
}
```

#### Parameters:
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `text` | string | Yes | Text to translate |
| `to` | string | Yes | Target language (en/ru/hy) |
| `from` | string | No | Source language. Default: `auto` |
| `audio` | boolean | No | Return TTS audio. Default: `false` |
| `voice` | string | No | Voice ID for TTS |

#### Request (cURL):
```bash
curl -X POST "http://localhost:5000/api/voice/translate" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "I need a taxi",
    "from": "en",
    "to": "hy",
    "audio": false
  }'
```

#### Response:
```json
{
  "text": "I need a taxi",
  "translation": "??? ???? ? ?????",
  "from": "en",
  "to": "hy"
}
```

---

## Voice Options

The API supports multiple voice personalities for TTS:

| Voice ID | Description | Best For |
|----------|-------------|----------|
| `alloy` | Balanced, neutral | General use |
| `echo` | Deep, resonant | Male voice |
| `fable` | Expressive | Storytelling |
| `onyx` | Strong, authoritative | Professional |
| `nova` | Friendly, warm | Customer service |
| `shimmer` | Soft, gentle | Calm responses |

### Default Voices by Language:
- **English (en)**: `alloy`
- **Russian (ru)**: `alloy`
- **Armenian (hy)**: `alloy`

You can override by providing the `voice` parameter.

---

## Language Detection & Intent Recognition

### Supported Intents:
1. **Taxi** - User wants to order a taxi
2. **Delivery** - User wants package delivery
3. **Schedule** - User wants to schedule a ride
4. **Chat** - General conversation

### Keywords by Language:

#### Taxi Intent:
- English: `taxi`
- Russian: `?????`
- Armenian: `?????`

#### Delivery Intent:
- English: `delivery`
- Russian: `????????`
- Armenian: `???????`

#### Schedule Intent:
- English: `schedule`
- Russian: `??????????`, `??????`
- Armenian: `???`

---

## Usage Examples

### Example 1: English Voice Upload

```bash
# Record audio: "I need a taxi to the airport"
# Save as: request_en.wav

curl -X POST "http://localhost:5000/api/voice/upload" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@request_en.wav" \
  -F "lang=en" \
  -F "audio=true" \
  -F "voice=nova"

# Returns: Audio response with booking confirmation
```

### Example 2: Russian Text Chat

```bash
curl -X POST "http://localhost:5000/api/voice/chat" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "??? ????? ????? ? ???????? ?????? ? 10 ????",
    "lang": "ru",
    "audio": true,
    "voice": "alloy"
  }'

# Returns: Audio response in Russian with scheduling confirmation
```

### Example 3: Armenian Translation

```bash
curl -X POST "http://localhost:5000/api/voice/translate" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "I need a taxi",
    "to": "hy",
    "audio": true,
    "voice": "shimmer"
  }'

# Returns: Armenian translation as audio
```

---

## Flutter/Dart Implementation

```dart
import 'package:http/http.dart' as http;
import 'package:http_parser/http_parser.dart';
import 'dart:io';

class VoiceService {
  final String baseUrl = 'http://localhost:5000';
  
  // Upload voice file
  Future<Map<String, dynamic>> uploadVoice({
    required String token,
    required File audioFile,
    required String language,
    bool requestAudio = false,
    String? voice,
  }) async {
    var request = http.MultipartRequest(
      'POST',
      Uri.parse('$baseUrl/api/voice/upload'),
    );
    
    request.headers['Authorization'] = 'Bearer $token';
    
    request.files.add(await http.MultipartFile.fromPath(
      'file',
      audioFile.path,
      contentType: MediaType('audio', 'wav'),
    ));
    
    request.fields['lang'] = language;
    request.fields['audio'] = requestAudio.toString();
    if (voice != null) {
      request.fields['voice'] = voice;
    }
    
    var response = await request.send();
    
    if (requestAudio) {
      // Handle audio response
      var audioBytes = await response.stream.toBytes();
      return {'audioData': audioBytes, 'type': 'audio'};
    } else {
      // Handle JSON response
      var responseBody = await response.stream.bytesToString();
      return jsonDecode(responseBody);
    }
  }
  
  // Text chat
  Future<dynamic> chat({
    required String token,
    required String text,
    required String language,
    bool requestAudio = false,
    String? voice,
  }) async {
    final response = await http.post(
      Uri.parse('$baseUrl/api/voice/chat'),
      headers: {
        'Authorization': 'Bearer $token',
        'Content-Type': 'application/json',
      },
      body: jsonEncode({
        'text': text,
        'lang': language,
        'audio': requestAudio,
        if (voice != null) 'voice': voice,
      }),
    );
    
    if (response.statusCode == 200) {
      if (requestAudio) {
        return response.bodyBytes; // Audio data
      } else {
        return jsonDecode(response.body); // JSON data
      }
    } else {
      throw Exception('Failed to chat: ${response.body}');
    }
  }
  
  // Translate
  Future<dynamic> translate({
    required String token,
    required String text,
    required String targetLang,
    String? sourceLang,
    bool requestAudio = false,
    String? voice,
  }) async {
    final response = await http.post(
      Uri.parse('$baseUrl/api/voice/translate'),
      headers: {
        'Authorization': 'Bearer $token',
        'Content-Type': 'application/json',
      },
      body: jsonEncode({
        'text': text,
        'to': targetLang,
        if (sourceLang != null) 'from': sourceLang,
        'audio': requestAudio,
        if (voice != null) 'voice': voice,
      }),
    );
    
    if (response.statusCode == 200) {
      if (requestAudio) {
        return response.bodyBytes;
      } else {
        return jsonDecode(response.body);
      }
    } else {
      throw Exception('Failed to translate: ${response.body}');
    }
  }
}

// Usage example
void main() async {
  final voiceService = VoiceService();
  final token = 'YOUR_JWT_TOKEN';
  
  // Example 1: Voice upload
  final audioFile = File('path/to/audio.wav');
  final result = await voiceService.uploadVoice(
    token: token,
    audioFile: audioFile,
    language: 'ru',
    requestAudio: true,
    voice: 'alloy',
  );
  
  // Example 2: Text chat
  final chatResult = await voiceService.chat(
    token: token,
    text: '??? ????? ?????',
    language: 'ru',
    requestAudio: false,
  );
  
  print('Reply: ${chatResult['reply']}');
}
```

---

## JavaScript/React Implementation

```javascript
class VoiceService {
  constructor(baseUrl = 'http://localhost:5000') {
    this.baseUrl = baseUrl;
  }
  
  // Upload voice file
  async uploadVoice(token, audioFile, language, requestAudio = false, voice = null) {
    const formData = new FormData();
    formData.append('file', audioFile);
    formData.append('lang', language);
    formData.append('audio', requestAudio.toString());
    if (voice) {
      formData.append('voice', voice);
    }
    
    const response = await fetch(`${this.baseUrl}/api/voice/upload`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${token}`
      },
      body: formData
    });
    
    if (!response.ok) {
      throw new Error('Failed to upload voice');
    }
    
    if (requestAudio) {
      return await response.blob(); // Audio data
    } else {
      return await response.json(); // JSON data
    }
  }
  
  // Text chat
  async chat(token, text, language, requestAudio = false, voice = null) {
    const response = await fetch(`${this.baseUrl}/api/voice/chat`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${token}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        text,
        lang: language,
        audio: requestAudio,
        voice
      })
    });
    
    if (!response.ok) {
      throw new Error('Failed to chat');
    }
    
    if (requestAudio) {
      return await response.blob();
    } else {
      return await response.json();
    }
  }
  
  // Translate
  async translate(token, text, targetLang, sourceLang = null, requestAudio = false, voice = null) {
    const response = await fetch(`${this.baseUrl}/api/voice/translate`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${token}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        text,
        to: targetLang,
        from: sourceLang,
        audio: requestAudio,
        voice
      })
    });
    
    if (!response.ok) {
      throw new Error('Failed to translate');
    }
    
    if (requestAudio) {
      return await response.blob();
    } else {
      return await response.json();
    }
  }
  
  // Play audio response
  playAudio(audioBlob) {
    const audioUrl = URL.createObjectURL(audioBlob);
    const audio = new Audio(audioUrl);
    audio.play();
    return audio;
  }
}

// Usage example
const voiceService = new VoiceService();
const token = 'YOUR_JWT_TOKEN';

// Example: Record and upload voice
navigator.mediaDevices.getUserMedia({ audio: true })
  .then(stream => {
    const mediaRecorder = new MediaRecorder(stream);
    const chunks = [];
    
    mediaRecorder.ondataavailable = (e) => chunks.push(e.data);
    mediaRecorder.onstop = async () => {
      const audioBlob = new Blob(chunks, { type: 'audio/wav' });
      const audioFile = new File([audioBlob], 'recording.wav');
      
      // Upload with Russian language and request audio response
      const result = await voiceService.uploadVoice(
        token,
        audioFile,
        'ru',
        true, // Request audio response
        'nova' // Use Nova voice
      );
      
      // Play audio response
      voiceService.playAudio(result);
    };
    
    mediaRecorder.start();
    setTimeout(() => mediaRecorder.stop(), 5000); // Record for 5 seconds
  });
```

---

## Testing in Swagger

### 1. Upload Voice File

1. Go to `/swagger`
2. Authorize with Bearer token
3. Navigate to `POST /api/voice/upload`
4. Click "Try it out"
5. Fill parameters:
   - **file**: Choose audio file (WAV/MP3)
   - **lang**: Select language (`en`, `ru`, or `hy`)
   - **audio**: Check if you want TTS response
   - **voice**: Enter voice ID (optional)
6. Click "Execute"
7. If `audio=true`, download the audio file

### 2. Text Chat

1. Navigate to `POST /api/voice/chat`
2. Click "Try it out"
3. Enter JSON body:
```json
{
  "text": "??? ????? ????? ? ????????",
  "lang": "ru",
  "audio": false,
  "voice": "alloy"
}
```
4. Click "Execute"

### 3. Translate

1. Navigate to `POST /api/voice/translate`
2. Click "Try it out"
3. Enter JSON body:
```json
{
  "text": "I need a taxi",
  "to": "hy",
  "audio": true,
  "voice": "nova"
}
```
4. Click "Execute"

---

## Error Handling

### Common Errors:

| Status | Error | Cause | Solution |
|--------|-------|-------|----------|
| 400 | No audio file provided | Missing file | Upload audio file |
| 400 | Unsupported language | Invalid lang code | Use: en, ru, or hy |
| 400 | Text is required | Missing text | Provide text parameter |
| 401 | Unauthorized | Missing/invalid token | Authenticate first |
| 502 | Transcription failed | OpenAI API error | Check API key |
| 502 | Chat failed | OpenAI API error | Check API key |
| 502 | TTS failed | OpenAI API error | Check API key |

---

## Best Practices

### 1. Language Selection
- Always specify language explicitly
- Use ISO codes: `en`, `ru`, `hy`
- Match user's interface language

### 2. Voice Selection
- Use `nova` or `shimmer` for friendlier tone
- Use `alloy` for neutral, balanced voice
- Use `onyx` for professional context

### 3. Audio Quality
- Upload WAV format for best results
- Minimum 16kHz sample rate
- Clear audio without background noise

### 4. Response Handling
- Check `intent` field for order creation
- Parse `order` object if created
- Display `reply` text to user
- Play audio if requested

### 5. Error Handling
- Always check response status
- Provide fallback for TTS failures
- Show transcription even if chat fails

---

## Configuration

### Required Environment Variables:

```bash
# OpenAI API Key (required for all voice features)
OpenAI__ApiKey=sk-your-openai-api-key-here
```

### Optional Configuration:

```json
{
  "OpenAI": {
    "ApiKey": "sk-your-api-key",
    "Model": "gpt-4o-mini",
    "TTSModel": "tts-1",
    "MaxTokens": 300
  }
}
```

---

## Summary

### Features:
? **3 Languages** - English, Russian, Armenian  
? **Voice Upload** - Audio file transcription  
? **Text Chat** - AI-powered conversation  
? **Translation** - Between supported languages  
? **TTS** - Text-to-Speech responses  
? **Voice Selection** - 6 different voices  
? **Intent Detection** - Taxi, delivery, schedule  
? **Order Creation** - Automatic from conversation  

### Parameters:
- `file` - Audio file for transcription
- `lang` - Language code (en/ru/hy)
- `audio` - Request TTS response (true/false)
- `voice` - Voice ID (alloy, nova, etc.)
- `text` - Text message for chat/translate

---

**Last Updated**: January 2026  
**API Version**: 1.0  
**Status**: ? Production Ready
