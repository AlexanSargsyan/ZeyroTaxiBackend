# ? Client/User Logout Endpoint

## Summary

Added a logout endpoint for regular clients/users at `/api/auth/logout`, matching the functionality of the driver logout endpoint at `/api/driver/logout`.

## New Endpoint

### Logout

**Endpoint:** `POST /api/auth/logout`

**Authorization:** Token can be provided via:
- Request body (`{"token": "jwt-token-here"}`)
- Authorization header (`Bearer jwt-token-here`)

**Content-Type:** `application/json`

**Request (Option 1 - Body):**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

**Request (Option 2 - Header):**
```
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Response (Success):**
```json
{
  "loggedOut": true
}
```

**Response (Errors):**

#### 401 Unauthorized - No Token
```json
{
  "error": "No token provided"
}
```

#### 401 Unauthorized - Invalid Token
```json
{
  "error": "Invalid subject in token"
}
```

#### 401 Unauthorized - User Not Found
```json
{
  "error": "User not found"
}
```

#### 401 Unauthorized - Token Expired
```json
{
  "error": "Token expired"
}
```

#### 500 Internal Server Error
```json
{
  "error": "Internal server error"
}
```

## How It Works

1. **Token Validation:** Validates the JWT token using the same key and issuer as authentication
2. **User Lookup:** Finds the user associated with the token
3. **Session Expiration:** Expires all active auth sessions for the user by setting:
   - `ExpiresAt` to current time
   - `Verified` to false
4. **Response:** Returns confirmation of logout

## Usage Examples

### cURL Examples

#### Logout with Token in Body
```bash
curl -X POST "http://localhost:5000/api/auth/logout" \
  -H "Content-Type: application/json" \
  -d '{"token": "YOUR_JWT_TOKEN"}'
```

#### Logout with Token in Header
```bash
curl -X POST "http://localhost:5000/api/auth/logout" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -H "Content-Type: application/json"
```

### Flutter/Dart Example

```dart
import 'package:http/http.dart' as http;
import 'dart:convert';

// Logout with token in body
Future<bool> logout(String token) async {
  final response = await http.post(
    Uri.parse('$baseUrl/api/auth/logout'),
    headers: {'Content-Type': 'application/json'},
    body: jsonEncode({'token': token}),
  );

  if (response.statusCode == 200) {
    final data = jsonDecode(response.body);
    return data['loggedOut'] == true;
  } else {
    throw Exception('Failed to logout');
  }
}

// Logout with token in header
Future<bool> logoutWithHeader(String token) async {
  final response = await http.post(
    Uri.parse('$baseUrl/api/auth/logout'),
    headers: {
      'Content-Type': 'application/json',
      'Authorization': 'Bearer $token',
    },
  );

  if (response.statusCode == 200) {
    final data = jsonDecode(response.body);
    return data['loggedOut'] == true;
  } else {
    throw Exception('Failed to logout');
  }
}
```

### JavaScript/React Example

```javascript
// Logout with token in body
async function logout(token) {
  const response = await fetch(`${baseUrl}/api/auth/logout`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ token: token })
  });

  if (!response.ok) {
    throw new Error('Failed to logout');
  }

  const data = await response.json();
  return data.loggedOut === true;
}

// Logout with token in header
async function logoutWithHeader(token) {
  const response = await fetch(`${baseUrl}/api/auth/logout`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    }
  });

  if (!response.ok) {
    throw new Error('Failed to logout');
  }

  const data = await response.json();
  return data.loggedOut === true;
}
```

### Python Example

```python
import requests

def logout(base_url, token):
    """Logout with token in body"""
    response = requests.post(
        f"{base_url}/api/auth/logout",
        json={"token": token}
    )
    
    if response.status_code == 200:
        return response.json().get('loggedOut') == True
    else:
        raise Exception(f"Failed to logout: {response.text}")

def logout_with_header(base_url, token):
    """Logout with token in header"""
    response = requests.post(
        f"{base_url}/api/auth/logout",
        headers={"Authorization": f"Bearer {token}"}
    )
    
    if response.status_code == 200:
        return response.json().get('loggedOut') == True
    else:
        raise Exception(f"Failed to logout: {response.text}")
```

## Complete Authentication Flow

### User Authentication Flow

```
1. Request Code
   POST /api/auth/request-code
   ? Returns: { sent: true, code: "123456", authSessionId: "guid" }

2. Verify Code
   POST /api/auth/verify
   ? Returns: { authSessionId: "guid" }

3. Get Token
   POST /api/auth/auth
   ? Returns: { token: "jwt-token", authSessionId: "guid" }

4. Use Token
   Add header: Authorization: Bearer {token}

5. Logout
   POST /api/auth/logout
   ? Returns: { loggedOut: true }
   ? All sessions expired
```

### Driver Authentication Flow

```
1. Request Code
   POST /api/driver/request-code
   ? Returns: { sent: true, code: "123456", authSessionId: "guid" }

2. Verify Code
   POST /api/driver/verify
   ? Returns: { authSessionId: "guid" }

3. Get Token
   POST /api/driver/auth
   ? Returns: { token: "jwt-token", authSessionId: "guid" }

4. Use Token
   Add header: Authorization: Bearer {token}

5. Logout
   POST /api/driver/logout
   ? Returns: { loggedOut: true }
   ? All sessions expired
```

## Comparison: User vs Driver Logout

| Feature | User Logout (`/api/auth/logout`) | Driver Logout (`/api/driver/logout`) |
|---------|----------------------------------|--------------------------------------|
| **Endpoint** | `POST /api/auth/logout` | `POST /api/driver/logout` |
| **Token Via Body** | ? Yes | ? Yes |
| **Token Via Header** | ? Yes | ? Yes |
| **Expires Sessions** | ? Yes | ? Yes |
| **User Check** | Regular users | Drivers only (`IsDriver=true`) |
| **Response** | `{ loggedOut: true }` | `{ loggedOut: true }` |

## Security Features

### ? Token Validation
- Validates JWT signature
- Checks token expiration
- Verifies issuer
- Validates signing key

### ? Session Management
- Expires all active sessions for the user
- Sets `Verified` flag to false
- Updates `ExpiresAt` to current time
- Prevents reuse of old sessions

### ? Error Handling
- Handles expired tokens gracefully
- Returns appropriate error messages
- Protects against token manipulation
- Returns 401 for unauthorized attempts

## Testing

### Test Cases

1. ? **Valid Token (Body)** - Should logout successfully
2. ? **Valid Token (Header)** - Should logout successfully
3. ? **No Token** - Should return 401 with "No token provided"
4. ? **Invalid Token** - Should return 401 with token error
5. ? **Expired Token** - Should return 401 with "Token expired"
6. ? **User Not Found** - Should return 401 with "User not found"
7. ? **Multiple Sessions** - Should expire all user sessions

### Testing Commands

```bash
# 1. Get a token first
TOKEN=$(curl -X POST "http://localhost:5000/api/auth/request-code" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "name": "Test User"}' | jq -r '.authSessionId')

CODE=$(curl -X POST "http://localhost:5000/api/auth/request-code" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "name": "Test User"}' | jq -r '.code')

curl -X POST "http://localhost:5000/api/auth/verify" \
  -H "Content-Type: application/json" \
  -d "{\"phone\": \"+37412345678\", \"code\": \"$CODE\", \"name\": \"Test User\"}"

JWT_TOKEN=$(curl -X POST "http://localhost:5000/api/auth/auth" \
  -H "Content-Type: application/json" \
  -d "{\"authSessionId\": \"$TOKEN\", \"code\": \"$CODE\"}" | jq -r '.token')

# 2. Test logout with token in body
curl -X POST "http://localhost:5000/api/auth/logout" \
  -H "Content-Type: application/json" \
  -d "{\"token\": \"$JWT_TOKEN\"}"

# 3. Test logout with token in header
curl -X POST "http://localhost:5000/api/auth/logout" \
  -H "Authorization: Bearer $JWT_TOKEN" \
  -H "Content-Type: application/json"

# 4. Test without token (should fail)
curl -X POST "http://localhost:5000/api/auth/logout" \
  -H "Content-Type: application/json"
```

## Database Impact

When a user logs out:

```sql
-- All active sessions for the user are expired
UPDATE AuthSessions 
SET ExpiresAt = CURRENT_TIMESTAMP, 
    Verified = 0
WHERE Phone = 'user_phone' 
  AND ExpiresAt > CURRENT_TIMESTAMP
```

## Best Practices

### Client-Side
1. ? Clear stored token after logout
2. ? Redirect to login page
3. ? Clear user data from memory
4. ? Handle logout errors gracefully

### Server-Side
1. ? Validate token before expiring sessions
2. ? Expire all user sessions
3. ? Log logout events (optional)
4. ? Return consistent error messages

## Implementation Notes

- **Token Acceptance:** Accepts token in both body and header for flexibility
- **Session Handling:** Expires all sessions for the user's phone number
- **Error Messages:** Returns detailed error information for debugging
- **Security:** Validates token signature and claims before processing

## Status

- ? **Build:** Successful
- ? **Endpoint:** Added to AuthController
- ? **Functionality:** Matches driver logout
- ? **Testing:** Ready for testing
- ? **Documentation:** Complete

---

**Last Updated:** January 16, 2026
**Status:** ? Complete and Ready for Testing
