# Veloconnect SMS Integration

## Overview

The Taxi API now uses **Veloconnect** (https://docs.veloconnect.me/) for sending SMS verification codes to phone numbers for both client and driver authentication.

## Configuration

### Required Settings

Add the following configuration to your `appsettings.json` or environment variables:

```json
{
  "Veloconnect": {
    "Username": "your_veloconnect_username",
    "Password": "your_veloconnect_password",
    "Originator": "TaxiApp"
  }
}
```

### Environment Variables (Alternative)

You can also set these via environment variables:

```bash
# PowerShell
$env:Veloconnect__Username = "your_username"
$env:Veloconnect__Password = "your_password"
$env:Veloconnect__Originator = "TaxiApp"

# Bash/Linux
export Veloconnect__Username="your_username"
export Veloconnect__Password="your_password"
export Veloconnect__Originator="TaxiApp"
```

## Affected Endpoints

All authentication endpoints now use Veloconnect to send SMS verification codes:

### Client Authentication (`/api/auth`)

1. **POST `/api/auth/request-code`**
   - Sends 6-digit verification code via SMS
   - Message format: `Your verification code is: {code}`

2. **POST `/api/auth/resend`**
   - Resends verification code via SMS
   - Message format: `Your verification code is: {code}`

3. **POST `/api/auth/verify`**
   - Verifies the SMS code (no SMS sent)

### Driver Authentication (`/api/driver`)

1. **POST `/api/driver/request-code`**
   - Sends 6-digit verification code via SMS
   - Message format: `Your driver verification code is: {code}`

2. **POST `/api/driver/resend`**
   - Resends driver verification code via SMS
   - Message format: `Your driver verification code is: {code}`

3. **POST `/api/driver/verify`**
   - Verifies the SMS code (no SMS sent)

## Implementation Details

### Service: `VeloconnectSmsService`

**Location:** `Services/VeloconnectSmsService.cs`

**Features:**
- Implements `ISmsService` interface
- Uses Veloconnect API v1
- Sends SMS via `https://api.veloconnect.me/api/v1/sms/send`
- Comprehensive error logging
- Graceful failure handling

**API Request Format:**
```json
{
  "username": "your_username",
  "password": "your_password",
  "originator": "TaxiApp",
  "recipient": "+37412345678",
  "message": "Your verification code is: 123456"
}
```

### Error Handling

- If Veloconnect credentials are not configured, a warning is logged and SMS is skipped
- If SMS sending fails, an error is logged but the request continues
- Verification codes are always returned in the API response for development/testing

## Testing

### 1. Test Client Authentication

```bash
# Request code
curl -X POST "http://localhost:5000/api/auth/request-code" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "name": "Test User"}'

# Expected response:
# {
#   "sent": true,
#   "code": "123456",
#   "authSessionId": "guid-here"
# }

# Resend code
curl -X POST "http://localhost:5000/api/auth/resend" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678"}'

# Verify code
curl -X POST "http://localhost:5000/api/auth/verify" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "code": "123456", "name": "Test User"}'
```

### 2. Test Driver Authentication

```bash
# Request code
curl -X POST "http://localhost:5000/api/driver/request-code" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "name": "Test Driver"}'

# Resend code
curl -X POST "http://localhost:5000/api/driver/resend" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678"}'

# Verify code
curl -X POST "http://localhost:5000/api/driver/verify" \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "code": "123456", "name": "Test Driver"}'
```

## Logging

The service logs all SMS operations:

- **Info:** Successful SMS sends
- **Warning:** Configuration missing or send failures
- **Error:** Exceptions during SMS sending

Example logs:
```
info: Taxi_API.Services.VeloconnectSmsService[0]
      Sending SMS via Veloconnect to +37412345678

info: Taxi_API.Services.VeloconnectSmsService[0]
      SMS sent successfully via Veloconnect to +37412345678. Response: {"status":"success","messageId":"123"}

warn: Taxi_API.Services.VeloconnectSmsService[0]
      Veloconnect not configured. Username or Password missing. Skipping SMS to +37412345678
```

## Migration from Twilio

The previous implementation used `TwilioSmsService`. Changes made:

1. ? Created `VeloconnectSmsService` implementing `ISmsService`
2. ? Updated `Program.cs` to register `VeloconnectSmsService` instead of `TwilioSmsService`
3. ? Updated `/api/auth/request-code` to use SMS service
4. ? Updated `/api/auth/resend` to use SMS service
5. ? Updated `/api/driver/request-code` to use SMS service
6. ? Updated `/api/driver/resend` to use SMS service
7. ? Added configuration section to `appsettings.json`
8. ? Added logging to `AuthController`

## Production Checklist

Before deploying to production:

- [ ] Set `Veloconnect:Username` in production configuration
- [ ] Set `Veloconnect:Password` in production configuration (use secrets manager)
- [ ] Set `Veloconnect:Originator` to your approved sender ID
- [ ] Test SMS delivery with real phone numbers
- [ ] Monitor logs for SMS failures
- [ ] Set `Auth:ReturnCodeInResponse` to `false` in production (for security)
- [ ] Ensure phone number normalization works for your target countries
- [ ] Consider rate limiting for SMS sends
- [ ] Set up alerts for SMS send failures

## Security Considerations

1. **Credentials Storage**
   - Never commit credentials to source control
   - Use Azure Key Vault, AWS Secrets Manager, or similar
   - Use environment variables in containers

2. **Code Visibility**
   - Codes are returned in API response for development
   - Set `Auth:ReturnCodeInResponse: false` in production
   - Codes expire after 10 minutes

3. **Rate Limiting**
   - Consider implementing rate limiting to prevent abuse
   - Veloconnect may have their own rate limits

4. **Phone Number Validation**
   - Uses `PhoneNumberValidator.Normalize()` for validation
   - Supports international formats

## API Reference

### Veloconnect API Documentation

- **Base URL:** `https://api.veloconnect.me`
- **Endpoint:** `POST /api/v1/sms/send`
- **Content-Type:** `application/json`
- **Documentation:** https://docs.veloconnect.me/

### Request Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `username` | string | Yes | Your Veloconnect username |
| `password` | string | Yes | Your Veloconnect password |
| `originator` | string | Yes | Sender ID (e.g., "TaxiApp") |
| `recipient` | string | Yes | Recipient phone number (E.164 format) |
| `message` | string | Yes | SMS message content |

### Response Format

Success response:
```json
{
  "status": "success",
  "messageId": "unique-message-id"
}
```

Error response:
```json
{
  "status": "error",
  "error": "Error description"
}
```

## Troubleshooting

### SMS Not Sending

1. **Check Configuration**
   ```bash
   # Check if credentials are set
   curl http://localhost:5000/api/auth/config-check
   ```

2. **Check Logs**
   - Look for warnings about missing configuration
   - Look for errors during SMS sending

3. **Verify Credentials**
   - Test credentials directly with Veloconnect API
   - Ensure account has sufficient credits

4. **Phone Number Format**
   - Ensure phone numbers are in E.164 format
   - Example: `+37412345678` (Armenia)

### Common Errors

| Error | Solution |
|-------|----------|
| "Veloconnect not configured" | Set Username and Password in configuration |
| "Failed to send SMS" | Check network connectivity and credentials |
| "Invalid phone format" | Use E.164 format: +[country code][number] |

## Support

- **Veloconnect Documentation:** https://docs.veloconnect.me/
- **Veloconnect Support:** Contact via their support portal

---

**Last Updated:** January 19, 2026  
**Version:** 1.0.0  
**Status:** ? Complete and Ready for Testing
