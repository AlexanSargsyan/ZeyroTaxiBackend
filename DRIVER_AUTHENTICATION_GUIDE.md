# Driver Authentication Guide

## Complete Authentication Flow (Steps 1-7)

This guide walks through the complete driver authentication process, from requesting a verification code to logging out.

---

## Prerequisites

- **Base URL**: `http://localhost:5000` (development) or your production URL
- **Content-Type**: `application/json` for all requests
- **Phone Format**: Must be a valid phone number (e.g., `+37412345678`)

---

## Step 1: Request Verification Code

### Endpoint
```
POST /api/driver/request-code
```

### Description
Request a 6-digit verification code to be sent via SMS to the driver's phone number.

### Request Body
```json
{
  "phone": "+37412345678",
  "name": "John Driver"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `phone` | string | Yes | Driver's phone number in international format |
| `name` | string | No | Driver's name (optional at this stage) |

### Response (Success - 200 OK)
```json
{
  "sent": true,
  "code": "123456"
}
```

### Response Fields
| Field | Type | Description |
|-------|------|-------------|
| `sent` | boolean | Indicates if SMS was sent successfully |
| `code` | string | 6-digit verification code (for testing/development) |

### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/request-code" \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37412345678",
    "name": "John Driver"
  }'
```

### Notes
- The code expires in **10 minutes**
- A new session is created in the database
- In production, the code is sent via SMS (Veloconnect)
- The code is returned in the response for testing purposes

---

## Step 2: Verify the Code

### Endpoint
```
POST /api/driver/verify
```

### Description
Verify the SMS code received and mark the session as verified. This creates or updates the driver user account.

### Request Body
```json
{
  "phone": "+37412345678",
  "code": "123456",
  "name": "John Driver"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `phone` | string | Yes | Driver's phone number (must match Step 1) |
| `code` | string | Yes | 6-digit code from Step 1 |
| `name` | string | No | Driver's name (will be saved/updated) |

### Response (Success - 200 OK)
```json
{
  "authSessionId": "550e8400-e29b-41d4-a716-446655440000"
}
```

### Response Fields
| Field | Type | Description |
|-------|------|-------------|
| `authSessionId` | string (GUID) | Session ID needed for Step 3 |

### Response (Error - 400 Bad Request)
```json
{
  "error": "Invalid or expired code"
}
```

### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/verify" \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37412345678",
    "code": "123456",
    "name": "John Driver"
  }'
```

### What Happens Behind the Scenes
1. Session is marked as `Verified = true`
2. User account is created or updated:
   - `IsDriver = true`
   - `PhoneVerified = true`
   - Name is saved if provided
3. Returns the `authSessionId` for next step

---

## Step 3: Get Authentication Token

### Endpoint
```
POST /api/driver/auth
```

### Description
Exchange the verified session for a JWT authentication token.

### Request Body
```json
{
  "authSessionId": "550e8400-e29b-41d4-a716-446655440000",
  "code": "123456"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `authSessionId` | string (GUID) | Yes | Session ID from Step 2 |
| `code` | string | Yes | Original 6-digit code |

### Response (Success - 200 OK)
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI1NTBlODQwMC1lMjliLTQxZDQtYTcxNi00NDY2NTU0NDAwMDAiLCJuYW1lIjoiSm9obiBEcml2ZXIiLCJwaG9uZSI6IiszNzQxMjM0NTY3OCIsImlhdCI6MTY0MDAwMDAwMCwiZXhwIjoxNjQwMDg2NDAwfQ.signature",
  "authSessionId": "550e8400-e29b-41d4-a716-446655440000"
}
```

### Response Fields
| Field | Type | Description |
|-------|------|-------------|
| `token` | string | JWT token for authentication |
| `authSessionId` | string (GUID) | Session ID (echo from request) |

### Response (Error - 400 Bad Request)
```json
{
  "error": "Session not verified"
}
```

### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/auth" \
  -H "Content-Type: application/json" \
  -d '{
    "authSessionId": "550e8400-e29b-41d4-a716-446655440000",
    "code": "123456"
  }'
```

### JWT Token Contents
The token contains the following claims:
- `sub`: User ID (GUID)
- `name`: Driver's name
- `phone`: Driver's phone number
- `iat`: Issued at timestamp
- `exp`: Expiration timestamp
- `iss`: Issuer ("TaxiApi")

### Token Expiration
- Default: **7 days**
- After expiration, use Step 4 (Login) to refresh

---

## Step 4: Login (Refresh Token)

### Endpoint
```
POST /api/driver/login
```

### Description
Validate an existing token and receive a fresh token with renewed expiration. Use this to "refresh" authentication before the current token expires.

### Option A: Token in Request Body

#### Request Body
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

#### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/login" \
  -H "Content-Type: application/json" \
  -d '{
    "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
  }'
```

### Option B: Token in Authorization Header

#### Headers
```
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

#### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/login" \
  -H "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..." \
  -H "Content-Type: application/json"
```

### Response (Success - 200 OK)
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...[NEW_TOKEN]",
  "authSessionId": ""
}
```

### Response (Error - 401 Unauthorized)

#### No Token Provided
```json
{
  "error": "No token provided"
}
```

#### Invalid Token
```json
{
  "error": "Invalid subject in token"
}
```

#### Token Expired
```json
{
  "error": "Token expired"
}
```

#### Not a Driver
```json
{
  "error": "No driver user for token subject"
}
```

### What This Does
1. Validates the current token (signature, expiration, issuer)
2. Verifies the user exists and `IsDriver = true`
3. Generates a **new token** with fresh expiration (7 days)
4. Returns the new token

### When to Use
- Before current token expires (proactive refresh)
- When resuming app after being closed
- After network reconnection
- When token is near expiration

---

## Step 5: Using the Token for API Calls

### Description
Use the token to authenticate protected driver endpoints.

### Protected Driver Endpoints
- `POST /api/driver/submit` - Submit driver profile with documents
- `GET /api/driver/status` - Get driver approval status
- `GET /api/driver/profile` - Get driver profile
- `GET /api/driver/car` - Get car information
- `PATCH /api/driver/location` - Update current location
- `POST /api/driver/stripe/onboard` - Stripe onboarding
- `POST /api/driver/identity/passport` - Upload passport
- `POST /api/driver/identity/license` - Upload license
- `POST /api/driver/identity/car-registration` - Upload car registration
- `GET /api/driver/identity` - Get identity information
- And many more...

### How to Include Token

#### Option A: Authorization Header (Recommended)
```bash
curl -X GET "http://localhost:5000/api/driver/profile" \
  -H "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
```

#### Example: Get Driver Profile
```bash
curl -X GET "http://localhost:5000/api/driver/profile" \
  -H "Authorization: Bearer YOUR_TOKEN_HERE" \
  -H "Content-Type: application/json"
```

#### Example: Update Location
```bash
curl -X PATCH "http://localhost:5000/api/driver/location" \
  -H "Authorization: Bearer YOUR_TOKEN_HERE" \
  -H "Content-Type: application/json" \
  -d '{
    "lat": 40.1872,
    "lng": 44.5152
  }'
```

### Response (Success)
Varies by endpoint - see Swagger documentation at `/swagger`

### Response (Error - 401 Unauthorized)
```json
{
  "error": "Unauthorized"
}
```

---

## Step 6: Resend Code (Optional)

### Endpoint
```
POST /api/driver/resend
```

### Description
Request a new verification code if the previous one expired or was not received.

### Request Body
```json
{
  "phone": "+37412345678"
}
```

### Response (Success - 200 OK)
```json
{
  "sent": true,
  "code": "789012"
}
```

### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/resend" \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37412345678"
  }'
```

### What This Does
1. Finds the most recent unverified session for the phone number
2. Generates a new 6-digit code
3. Extends expiration by 10 minutes
4. Sends new code via SMS

### When to Use
- User didn't receive the SMS
- Code expired (after 10 minutes)
- User entered wrong code multiple times

---

## Step 7: Logout

### Endpoint
```
POST /api/driver/logout
```

### Description
Invalidate the current token and all active sessions for the driver. Forces re-authentication from Step 1.

### Option A: Token in Request Body

#### Request Body
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

#### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/logout" \
  -H "Content-Type: application/json" \
  -d '{
    "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
  }'
```

### Option B: Token in Authorization Header

#### Headers
```
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

#### cURL Example
```bash
curl -X POST "http://localhost:5000/api/driver/logout" \
  -H "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..." \
  -H "Content-Type: application/json"
```

### Response (Success - 200 OK)
```json
{
  "loggedOut": true
}
```

### Response (Error - 401 Unauthorized)

#### No Token
```json
{
  "error": "No token provided"
}
```

#### Invalid Token
```json
{
  "error": "Invalid subject in token"
}
```

#### Token Expired
```json
{
  "error": "Token expired"
}
```

### What This Does
1. Validates the token
2. Finds all active sessions for the driver's phone number
3. Sets `ExpiresAt = NOW()` for all sessions
4. Sets `Verified = false` for all sessions
5. Prevents token reuse

### When to Use
- User explicitly logs out
- Security: Suspicious activity detected
- User switches accounts
- Password/phone change (force re-verification)

### After Logout
The driver must go through the complete authentication flow again:
1. Request code (Step 1)
2. Verify code (Step 2)
3. Get token (Step 3)

---

## Complete Flow Example (All Steps)

### Bash Script
```bash
#!/bin/bash

BASE_URL="http://localhost:5000"
PHONE="+37412345678"
NAME="John Driver"

echo "Step 1: Request verification code"
RESPONSE_1=$(curl -s -X POST "$BASE_URL/api/driver/request-code" \
  -H "Content-Type: application/json" \
  -d "{\"phone\": \"$PHONE\", \"name\": \"$NAME\"}")
echo "$RESPONSE_1"
CODE=$(echo "$RESPONSE_1" | jq -r '.code')

echo -e "\nStep 2: Verify the code"
RESPONSE_2=$(curl -s -X POST "$BASE_URL/api/driver/verify" \
  -H "Content-Type: application/json" \
  -d "{\"phone\": \"$PHONE\", \"code\": \"$CODE\", \"name\": \"$NAME\"}")
echo "$RESPONSE_2"
AUTH_SESSION_ID=$(echo "$RESPONSE_2" | jq -r '.authSessionId')

echo -e "\nStep 3: Get authentication token"
RESPONSE_3=$(curl -s -X POST "$BASE_URL/api/driver/auth" \
  -H "Content-Type: application/json" \
  -d "{\"authSessionId\": \"$AUTH_SESSION_ID\", \"code\": \"$CODE\"}")
echo "$RESPONSE_3"
TOKEN=$(echo "$RESPONSE_3" | jq -r '.token')

echo -e "\nStep 4: Login (refresh token)"
RESPONSE_4=$(curl -s -X POST "$BASE_URL/api/driver/login" \
  -H "Content-Type: application/json" \
  -d "{\"token\": \"$TOKEN\"}")
echo "$RESPONSE_4"
NEW_TOKEN=$(echo "$RESPONSE_4" | jq -r '.token')

echo -e "\nStep 5: Use token to get profile"
RESPONSE_5=$(curl -s -X GET "$BASE_URL/api/driver/profile" \
  -H "Authorization: Bearer $NEW_TOKEN" \
  -H "Content-Type: application/json")
echo "$RESPONSE_5"

echo -e "\nStep 6: Resend code (optional - creating new session for demo)"
RESPONSE_6=$(curl -s -X POST "$BASE_URL/api/driver/resend" \
  -H "Content-Type: application/json" \
  -d "{\"phone\": \"$PHONE\"}")
echo "$RESPONSE_6"

echo -e "\nStep 7: Logout"
RESPONSE_7=$(curl -s -X POST "$BASE_URL/api/driver/logout" \
  -H "Content-Type: application/json" \
  -d "{\"token\": \"$NEW_TOKEN\"}")
echo "$RESPONSE_7"

echo -e "\nComplete! Driver has been authenticated and logged out."
```

---

## Mobile App Integration Examples

### Flutter/Dart Example

```dart
import 'package:http/http.dart' as http;
import 'dart:convert';

class DriverAuthService {
  final String baseUrl = 'http://localhost:5000';
  String? _token;

  // Step 1: Request Code
  Future<Map<String, dynamic>> requestCode(String phone, String? name) async {
    final response = await http.post(
      Uri.parse('$baseUrl/api/driver/request-code'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'phone': phone, 'name': name}),
    );
    
    if (response.statusCode == 200) {
      return jsonDecode(response.body);
    } else {
      throw Exception('Failed to request code: ${response.body}');
    }
  }

  // Step 2: Verify Code
  Future<String> verifyCode(String phone, String code, String? name) async {
    final response = await http.post(
      Uri.parse('$baseUrl/api/driver/verify'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'phone': phone, 'code': code, 'name': name}),
    );
    
    if (response.statusCode == 200) {
      final data = jsonDecode(response.body);
      return data['authSessionId'];
    } else {
      throw Exception('Failed to verify code: ${response.body}');
    }
  }

  // Step 3: Get Token
  Future<String> getToken(String authSessionId, String code) async {
    final response = await http.post(
      Uri.parse('$baseUrl/api/driver/auth'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'authSessionId': authSessionId, 'code': code}),
    );
    
    if (response.statusCode == 200) {
      final data = jsonDecode(response.body);
      _token = data['token'];
      return _token!;
    } else {
      throw Exception('Failed to get token: ${response.body}');
    }
  }

  // Step 4: Login (Refresh Token)
  Future<String> login(String token) async {
    final response = await http.post(
      Uri.parse('$baseUrl/api/driver/login'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'token': token}),
    );
    
    if (response.statusCode == 200) {
      final data = jsonDecode(response.body);
      _token = data['token'];
      return _token!;
    } else {
      throw Exception('Failed to login: ${response.body}');
    }
  }

  // Step 5: Make Authenticated Request
  Future<Map<String, dynamic>> getProfile() async {
    if (_token == null) throw Exception('Not authenticated');
    
    final response = await http.get(
      Uri.parse('$baseUrl/api/driver/profile'),
      headers: {
        'Authorization': 'Bearer $_token',
        'Content-Type': 'application/json',
      },
    );
    
    if (response.statusCode == 200) {
      return jsonDecode(response.body);
    } else {
      throw Exception('Failed to get profile: ${response.body}');
    }
  }

  // Step 6: Resend Code
  Future<Map<String, dynamic>> resendCode(String phone) async {
    final response = await http.post(
      Uri.parse('$baseUrl/api/driver/resend'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'phone': phone}),
    );
    
    if (response.statusCode == 200) {
      return jsonDecode(response.body);
    } else {
      throw Exception('Failed to resend code: ${response.body}');
    }
  }

  // Step 7: Logout
  Future<bool> logout() async {
    if (_token == null) return true;
    
    final response = await http.post(
      Uri.parse('$baseUrl/api/driver/logout'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'token': _token}),
    );
    
    if (response.statusCode == 200) {
      _token = null;
      return true;
    } else {
      throw Exception('Failed to logout: ${response.body}');
    }
  }

  // Complete Flow
  Future<void> completeAuthFlow(String phone, String name) async {
    // Step 1
    final codeResponse = await requestCode(phone, name);
    final code = codeResponse['code'];
    
    // Step 2
    final authSessionId = await verifyCode(phone, code, name);
    
    // Step 3
    final token = await getToken(authSessionId, code);
    
    print('Authenticated! Token: $token');
  }
}

// Usage Example
void main() async {
  final authService = DriverAuthService();
  
  try {
    // Complete authentication
    await authService.completeAuthFlow('+37412345678', 'John Driver');
    
    // Get profile
    final profile = await authService.getProfile();
    print('Profile: $profile');
    
    // Logout
    await authService.logout();
    print('Logged out successfully');
  } catch (e) {
    print('Error: $e');
  }
}
```

---

## Error Handling

### Common Errors

| Status Code | Error Message | Cause | Solution |
|-------------|--------------|-------|----------|
| 400 | Phone is required | Missing phone in request | Include phone field |
| 400 | Invalid phone format | Phone format invalid | Use international format (+XXX...) |
| 400 | Invalid or expired code | Wrong code or expired | Request new code or check input |
| 400 | Session not verified | Skipped verify step | Call /verify before /auth |
| 401 | No token provided | Token missing | Include token in body or header |
| 401 | Token expired | Token lifetime exceeded | Use /login to refresh |
| 401 | Invalid subject in token | Malformed token | Get new token from /auth |
| 401 | No driver user for token subject | User not marked as driver | Complete verification process |
| 500 | Internal server error | Server issue | Check logs, contact support |

---

## Security Best Practices

### For Drivers/Users
1. ? **Store tokens securely** (encrypted storage, not plain text)
2. ? **Don't share tokens** with anyone
3. ? **Logout when done** to invalidate sessions
4. ? **Use HTTPS** in production (not HTTP)
5. ? **Refresh tokens proactively** before expiration

### For Developers
1. ? **Validate token on every request** (already implemented)
2. ? **Use strong JWT secret** (`Jwt:Key` in config)
3. ? **Enable HTTPS** in production
4. ? **Log security events** (failed logins, logouts)
5. ? **Rate limit** authentication endpoints
6. ? **Monitor for suspicious activity**
7. ? **Rotate JWT secrets periodically**

---

## Token Lifecycle

```
???????????????????
?  Request Code   ? Step 1
???????????????????
         ?
         ?
???????????????????
?  Verify Code    ? Step 2
???????????????????
         ?
         ?
???????????????????
?   Get Token     ? Step 3 ? Token issued (7 days)
???????????????????
         ?
         ?
???????????????????
?   Use Token     ? Step 5 ? Make API calls
???????????????????
         ?
         ??????????????????????????
         ?                        ?
         ?                        ?
???????????????????      ???????????????????
? Refresh Token   ?      ?     Logout      ?
?    (Login)      ?      ?                 ?
?     Step 4      ?      ?     Step 7      ?
???????????????????      ???????????????????
         ?                        ?
         ? New token (7 days)     ? All sessions expired
         ?                        ?
         ??????????????????????????
                  ?
                  ?
         ???????????????????
         ? Start Over From ?
         ?     Step 1      ?
         ???????????????????
```

---

## Testing Checklist

### ? Step 1: Request Code
- [ ] Valid phone number returns code
- [ ] Invalid phone format returns 400
- [ ] Missing phone returns 400
- [ ] Code expires after 10 minutes

### ? Step 2: Verify Code
- [ ] Valid code marks session as verified
- [ ] Invalid code returns 400
- [ ] Expired code returns 400
- [ ] Creates user with `IsDriver = true`
- [ ] Creates user with `PhoneVerified = true`

### ? Step 3: Get Token
- [ ] Valid session returns JWT token
- [ ] Unverified session returns 400
- [ ] Invalid session ID returns 400
- [ ] Token contains correct claims

### ? Step 4: Login
- [ ] Valid token returns new token
- [ ] Expired token returns 401
- [ ] Invalid token returns 401
- [ ] Non-driver user returns 401
- [ ] Works with body and header

### ? Step 5: Use Token
- [ ] Valid token accesses protected endpoints
- [ ] Invalid token returns 401
- [ ] Expired token returns 401
- [ ] Missing token returns 401

### ? Step 6: Resend Code
- [ ] Valid phone gets new code
- [ ] Invalid phone returns 400
- [ ] No active session returns 400
- [ ] Extends expiration time

### ? Step 7: Logout
- [ ] Valid token logs out successfully
- [ ] Expires all user sessions
- [ ] Token cannot be reused after logout
- [ ] Works with body and header

---

## Troubleshooting

### Issue: "No token provided"
**Solution**: Include token in either:
- Request body: `{"token": "..."}`
- Authorization header: `Bearer ...`

### Issue: "Token expired"
**Solution**: Use `/driver/login` endpoint with expired token to refresh, or go through Steps 1-3 again.

### Issue: "Session not verified"
**Solution**: Call `/driver/verify` before calling `/driver/auth`.

### Issue: "Invalid or expired code"
**Solution**: Use `/driver/resend` to get a new code.

### Issue: "No driver user for token subject"
**Solution**: Ensure user completed `/driver/verify` which sets `IsDriver = true`.

---

## API Reference Summary

| Step | Method | Endpoint | Auth Required | Purpose |
|------|--------|----------|---------------|---------|
| 1 | POST | `/api/driver/request-code` | No | Request SMS code |
| 2 | POST | `/api/driver/verify` | No | Verify SMS code |
| 3 | POST | `/api/driver/auth` | No | Get JWT token |
| 4 | POST | `/api/driver/login` | Token | Refresh token |
| 5 | * | `/api/driver/*` | Token (Bearer) | Protected endpoints |
| 6 | POST | `/api/driver/resend` | No | Resend code |
| 7 | POST | `/api/driver/logout` | Token | Invalidate sessions |

---

## Additional Resources

- **Swagger UI**: `http://localhost:5000/swagger` - Interactive API documentation
- **JWT Debugger**: https://jwt.io - Decode and inspect tokens
- **Phone Format Validator**: Use international format (+country code + number)

---

## Support

For issues or questions:
1. Check this guide first
2. Review error messages carefully
3. Test with cURL examples provided
4. Check Swagger documentation
5. Review server logs for detailed error information

---

**Last Updated**: January 2026  
**Version**: 1.0  
**Status**: ? Production Ready
