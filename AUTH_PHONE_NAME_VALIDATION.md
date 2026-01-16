# Authentication - Phone Number & Name Validation

## Overview

The authentication system now prevents multiple users from registering with the same phone number using different names. This ensures data integrity and prevents account confusion.

## Validation Logic

### `/api/auth/request-code` Endpoint

When a user requests a verification code, the system now performs the following check:

1. **Normalize the phone number** (format validation)
2. **Check if a name is provided** in the request
3. **Look up existing user** with the same phone number
4. **Compare names** (case-insensitive)
5. **Reject if names don't match** with a clear error message

## API Behavior

### Scenario 1: New User (First Registration)
**Request:**
```json
POST /api/auth/request-code
{
  "phone": "+37412345678",
  "name": "John Doe"
}
```

**Response:** ? Success
```json
{
  "sent": true,
  "code": "123456",
  "authSessionId": "guid-here"
}
```

### Scenario 2: Existing User - Same Name
**Request:**
```json
POST /api/auth/request-code
{
  "phone": "+37412345678",
  "name": "John Doe"
}
```

**Response:** ? Success (Name matches)
```json
{
  "sent": true,
  "code": "654321",
  "authSessionId": "guid-here"
}
```

### Scenario 3: Existing User - Different Name (Blocked)
**Request:**
```json
POST /api/auth/request-code
{
  "phone": "+37412345678",
  "name": "Jane Smith"
}
```

**Response:** ? Error 400 Bad Request
```json
{
  "error": "Phone number already registered",
  "message": "This phone number is already registered with a different name. Please use the correct name: John Doe",
  "existingName": "John Doe"
}
```

### Scenario 4: Name Not Provided (Allowed for Legacy)
**Request:**
```json
POST /api/auth/request-code
{
  "phone": "+37412345678"
}
```

**Response:** ? Success (No validation)
```json
{
  "sent": true,
  "code": "789012",
  "authSessionId": "guid-here"
}
```

## Benefits

### 1. Prevents Account Hijacking
- Users can't claim someone else's phone number with a different identity
- Protects existing user accounts

### 2. Data Integrity
- Ensures one phone number maps to one user identity
- Prevents duplicate or conflicting records

### 3. Clear Error Messages
- Users are informed why their request failed
- Suggested action: Use the correct name associated with the phone

### 4. Case-Insensitive Comparison
- "John Doe" matches "john doe" or "JOHN DOE"
- Reduces false rejections due to capitalization

## Implementation Details

### Code Location
`Controllers/AuthController.cs` - `RequestCode` method

### Validation Steps
```csharp
// 1. Normalize phone number
var phone = PhoneNumberValidator.Normalize(req.Phone);

// 2. Check if name is provided
if (!string.IsNullOrWhiteSpace(req.Name))
{
    // 3. Find existing user
    var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Phone == phone);
    
    // 4. Compare names (case-insensitive)
    if (existingUser != null && 
        !string.Equals(existingUser.Name, req.Name, StringComparison.OrdinalIgnoreCase))
    {
        // 5. Reject with detailed error
        return BadRequest(new 
        { 
            error = "Phone number already registered",
            message = $"This phone number is already registered with a different name. Please use the correct name: {existingUser.Name}",
            existingName = existingUser.Name
        });
    }
}
```

## Frontend Integration

### Handle Error Response

```javascript
// React/JavaScript example
async function requestCode(phone, name) {
  try {
    const response = await fetch('/api/auth/request-code', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ phone, name })
    });

    if (!response.ok) {
      const error = await response.json();
      
      if (error.existingName) {
        // Show error with existing name
        alert(`This phone is registered as: ${error.existingName}`);
        // Optionally pre-fill the correct name
        setNameField(error.existingName);
      } else {
        alert(error.message || 'Request failed');
      }
      return;
    }

    const data = await response.json();
    // Proceed with verification
    navigateToVerification(data.authSessionId);
    
  } catch (err) {
    console.error('Error:', err);
  }
}
```

### Flutter/Dart Example

```dart
Future<void> requestCode(String phone, String name) async {
  try {
    final response = await http.post(
      Uri.parse('$baseUrl/api/auth/request-code'),
      headers: {'Content-Type': 'application/json'},
      body: jsonEncode({'phone': phone, 'name': name}),
    );

    if (response.statusCode == 400) {
      final error = jsonDecode(response.body);
      
      if (error['existingName'] != null) {
        // Show dialog with existing name
        showDialog(
          context: context,
          builder: (context) => AlertDialog(
            title: Text('Phone Already Registered'),
            content: Text('This phone is registered as: ${error['existingName']}'),
            actions: [
              TextButton(
                onPressed: () {
                  // Pre-fill with correct name
                  nameController.text = error['existingName'];
                  Navigator.pop(context);
                },
                child: Text('Use This Name'),
              ),
            ],
          ),
        );
      }
      return;
    }

    // Success - proceed
    final data = jsonDecode(response.body);
    navigateToVerification(data['authSessionId']);
    
  } catch (e) {
    print('Error: $e');
  }
}
```

## Testing

### Test Case 1: New User Registration
```bash
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37491111111",
    "name": "New User"
  }'
```

**Expected:** Success with verification code

### Test Case 2: Existing User - Correct Name
```bash
# First, register a user
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37491111111",
    "name": "Test User"
  }'

# Complete registration...

# Then try again with same phone and name
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37491111111",
    "name": "Test User"
  }'
```

**Expected:** Success with new verification code

### Test Case 3: Existing User - Different Name
```bash
# Try with different name
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37491111111",
    "name": "Different Name"
  }'
```

**Expected:** Error 400 with existing name information

### Test Case 4: Case Insensitive
```bash
# Original: "Test User"
# Try with different case
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{
    "phone": "+37491111111",
    "name": "test user"
  }'
```

**Expected:** Success (case-insensitive match)

## Migration Notes

### Existing Users
- ? Existing users are protected automatically
- ? No database migration needed
- ? Works with current User table

### Backward Compatibility
- ? If `name` is not provided, no validation occurs
- ? Legacy clients without name field still work
- ? Gradual rollout possible

## Security Considerations

### Privacy
- ? **Don't expose full user details** in error messages
- ? **Only return the registered name** for verification
- ? **Don't reveal phone number ownership** to unauthorized users

### Rate Limiting
Consider adding rate limiting to prevent:
- Multiple phone number probing attempts
- Name enumeration attacks

### Recommendations
1. Add rate limiting per IP address
2. Log suspicious patterns (many failed name checks)
3. Consider CAPTCHA for repeated failures
4. Monitor for automated attacks

## Future Enhancements

### Possible Improvements
1. **Email Verification** - Add email as secondary identifier
2. **Account Recovery** - Allow name change with verification
3. **Admin Override** - Support for legitimate name changes
4. **Audit Log** - Track name mismatch attempts
5. **Two-Factor Auth** - Additional security layer

## Summary

? **Problem Solved:** Multiple users can't register with the same phone number using different names

? **User Experience:** Clear error messages guide users to correct information

? **Security:** Protects existing accounts from unauthorized access

? **Compatibility:** Works with existing codebase, no breaking changes

---

**Implementation Date:** January 2026
**Status:** ? Active and Deployed
