# Voice AI Input/Output Mode - Update Summary

## ? What Changed

Updated the Voice AI system to automatically match **output format to input format**:

### Before:
- Both endpoints had `audio` boolean parameter
- User had to specify if they wanted audio response
- Could mix input/output types (voice input, text output)

### After:
- **Voice input automatically gets voice output**
- **Text input automatically gets text output**  
- Natural, intuitive behavior
- No `audio` parameter needed

---

## ?? Updated Endpoints

### 1. POST /api/voice/upload (Voice ? Voice)

**Input:** Audio file  
**Output:** Audio file (always)

#### Parameters:
```
file:  [Audio File]  (Required)
lang:  en|ru|hy      (Optional, default: en)
voice: alloy|nova... (Optional)
```

#### Behavior:
1. Transcribe audio to text
2. Process with AI
3. Generate audio response
4. Return audio file with metadata in headers

#### Response Headers:
- `X-Transcription`: What user said
- `X-Intent`: Detected intent
- `X-Language`: Language used
- `X-Order-Created`: If order created
- `X-Order-Id`: Order ID

---

### 2. POST /api/voice/chat (Text ? Text)

**Input:** JSON text  
**Output:** JSON response (always)

#### Request Body:
```json
{
  "text": "I need a taxi",
  "lang": "en",
  "voice": "nova"
}
```

#### Response Body:
```json
{
  "text": "I need a taxi",
  "intent": "taxi",
  "reply": "Sure! I'll help you...",
  "order": { ... },
  "language": "en"
}
```

#### Behavior:
1. Process text with AI
2. Detect intent
3. Generate text response
4. Return JSON (no audio)

---

## ?? Comparison

| Feature | Voice Upload | Text Chat |
|---------|--------------|-----------|
| **Endpoint** | `/api/voice/upload` | `/api/voice/chat` |
| **Input** | Audio file | JSON text |
| **Output** | Audio file | JSON text |
| **Metadata** | HTTP headers | JSON fields |
| **Use Case** | Driving, hands-free | Office, quiet places |

---

## ?? User Experience

### Voice Mode (Hands-Free):
```
User: [Records voice] "??? ????? ?????"
  ?
API: [Transcribes] ? [AI processes] ? [TTS generates]
  ?
User: [Plays audio] "??????, ? ??????..."
```

### Text Mode (Chat Interface):
```
User: [Types] "I need a taxi"
  ?
API: [AI processes] ? [Generates text]
  ?
User: [Reads] "Sure! I'll help you..."
```

---

## ?? Code Examples

### Flutter - Voice Mode:
```dart
// Send voice, get voice back
var request = http.MultipartRequest('POST', Uri.parse('$baseUrl/api/voice/upload'));
request.headers['Authorization'] = 'Bearer $token';
request.files.add(await http.MultipartFile.fromPath('file', audioFile.path));
request.fields['lang'] = 'ru';

var response = await request.send();
var audioBytes = await response.stream.toBytes();

// Play audio response
playAudio(audioBytes);
```

### Flutter - Text Mode:
```dart
// Send text, get text back
final response = await http.post(
  Uri.parse('$baseUrl/api/voice/chat'),
  headers: {
    'Authorization': 'Bearer $token',
    'Content-Type': 'application/json',
  },
  body: jsonEncode({
    'text': '??? ????? ?????',
    'lang': 'ru',
  }),
);

final data = jsonDecode(response.body);
print('Reply: ${data['reply']}');
```

---

## ?? Migration Guide

### If you were using the old `audio` parameter:

**Old Code:**
```dart
// Voice with audio response
await uploadVoice(file, lang: 'en', audio: true);

// Voice with text response
await uploadVoice(file, lang: 'en', audio: false);
```

**New Code:**
```dart
// Voice always gets audio response
await uploadVoice(file, lang: 'en');

// For text response, use text endpoint instead
await sendTextChat('my message', lang: 'en');
```

---

## ? Benefits

1. **More Intuitive** - No need to think about output format
2. **Consistent** - Same behavior across languages
3. **Natural** - Matches user expectations
4. **Simpler API** - Fewer parameters to configure
5. **Better UX** - Voice stays voice, text stays text

---

## ?? Files Modified

| File | Changes |
|------|---------|
| `Controllers/VoiceController.cs` | Removed `audio` parameter from upload |
| `Controllers/VoiceController.cs` | Voice upload always returns audio |
| `Controllers/VoiceController.cs` | Text chat always returns JSON |
| `Controllers/VoiceController.cs` | Added response headers for metadata |

---

## ?? Documentation Created

| File | Description |
|------|-------------|
| `VOICE_INPUT_OUTPUT_MODE_GUIDE.md` | Complete guide for input/output modes |

---

## ?? Next Steps

1. **Stop Application** (currently running)
2. **Build**: `dotnet build`
3. **Run**: `dotnet run --project TaxiApi.csproj`
4. **Test Voice**: Upload audio file in Swagger
5. **Test Text**: Send JSON to /chat endpoint

---

**Status**: ? Complete  
**Breaking Change**: ?? Removed `audio` parameter  
**Migration**: Simple - use correct endpoint for input type  
**Ready**: ? Yes
