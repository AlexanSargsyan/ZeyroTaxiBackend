# Environment-Specific Configuration for Auth Codes

## Overview

The authentication system now properly handles environment-specific behavior for returning verification codes in API responses.

## Configuration Structure

### Development Environment (`appsettings.Development.json`)
```json
{
  "Auth": {
    "ReturnCodeInResponse": true
  }
}
```
- ? **Returns code in response** for easy testing
- ? Used when `ASPNETCORE_ENVIRONMENT=Development`
- ? Local development with `dotnet run`

### Production Environment (`appsettings.Production.json`)
```json
{
  "Auth": {
    "ReturnCodeInResponse": false
  }
}
```
- ? **Does NOT return code** for security
- ? Used when `ASPNETCORE_ENVIRONMENT=Production`
- ? Docker/AWS deployments

### Base Configuration (`appsettings.json`)
```json
{
  // No Auth section - relies on environment-specific settings
}
```
- ? Doesn't specify `ReturnCodeInResponse`
- ? Defaults to `false` if not set
- ? Environment-specific configs override this

## How It Works

### Code Implementation

```csharp
// Check if we should return the code in response (for development/testing only)
var returnCodeInResponse = _config.GetValue<bool>("Auth:ReturnCodeInResponse", false);

if (returnCodeInResponse)
{
    return Ok(new { Sent = true, Code = code, AuthSessionId = session.Id.ToString() });
}

// Production: Don't return code for security
return Ok(new { Sent = true });
```

### Key Changes from Previous Implementation

| Before | After |
|--------|-------|
| Used `#if DEBUG` compiler directive | Uses configuration setting |
| Mixed compile-time and runtime logic | Pure runtime configuration |
| Less flexible | Environment-specific control |
| Harder to test production behavior | Can test both modes easily |

## Environment Variable Override

You can also override via environment variables (useful for Docker):

```bash
# Enable code return in response
export Auth__ReturnCodeInResponse=true

# Or in docker-compose.yml
environment:
  - Auth__ReturnCodeInResponse=true
```

## Response Examples

### Development Response
```json
{
  "sent": true,
  "code": "645976",
  "authSessionId": "0534524e-b1a5-4662-b9fc-c275db2a53a6"
}
```

### Production Response
```json
{
  "sent": true
}
```

## Security Benefits

### ? Production Security
- Code is **never** returned in production API response
- Prevents code interception via network monitoring
- Forces proper SMS/email verification flow

### ? Development Convenience
- Code is **always** returned in development
- Easy to test without SMS/email setup
- Faster development cycle

### ? Flexible Testing
- Can enable/disable via configuration
- Test production behavior locally
- No code recompilation needed

## Docker/AWS Deployment

### Dockerfile
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5000

# ... build steps ...

# Set environment to Production
ENV ASPNETCORE_ENVIRONMENT=Production
```

### docker-compose.yml (Production)
```yaml
services:
  taxi-api:
    image: taxi-api:latest
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      # Auth:ReturnCodeInResponse defaults to false
    ports:
      - "5000:5000"
```

### docker-compose.yml (Staging/Testing)
```yaml
services:
  taxi-api:
    image: taxi-api:latest
    environment:
      - ASPNETCORE_ENVIRONMENT=Staging
      - Auth__ReturnCodeInResponse=true  # Enable for testing
    ports:
      - "5000:5000"
```

## Testing Different Environments

### Test Development Behavior
```bash
# Run in Development mode
ASPNETCORE_ENVIRONMENT=Development dotnet run

# Test request
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "name": "Test User"}'

# Expected: Returns code and authSessionId
```

### Test Production Behavior
```bash
# Run in Production mode
ASPNETCORE_ENVIRONMENT=Production dotnet run

# Test request
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "name": "Test User"}'

# Expected: Returns only {"sent": true}
```

### Test with Environment Variable Override
```bash
# Run in Production but enable code return for testing
ASPNETCORE_ENVIRONMENT=Production Auth__ReturnCodeInResponse=true dotnet run

# Test request
curl -X POST http://localhost:5000/api/auth/request-code \
  -H "Content-Type: application/json" \
  -d '{"phone": "+37412345678", "name": "Test User"}'

# Expected: Returns code and authSessionId (override works)
```

## Configuration Precedence

ASP.NET Core loads configuration in this order (later sources override earlier):

1. `appsettings.json` (base configuration)
2. `appsettings.{Environment}.json` (environment-specific)
3. Environment variables
4. Command-line arguments

### Example:

```
appsettings.json: (no Auth section)
  ?
appsettings.Production.json: Auth:ReturnCodeInResponse = false
  ?
Environment Variable: Auth__ReturnCodeInResponse = true
  ?
Final Value: true (environment variable wins)
```

## Troubleshooting

### Issue: Still getting code in AWS/Production

**Cause:** Environment not set correctly

**Solution:**
```bash
# Check current environment
echo $ASPNETCORE_ENVIRONMENT

# Should be "Production"
# If not, set it:
export ASPNETCORE_ENVIRONMENT=Production
```

### Issue: Not getting code in Development

**Cause:** `appsettings.Development.json` missing or incorrect

**Solution:**
```bash
# Verify file exists
cat appsettings.Development.json

# Should contain:
# {
#   "Auth": {
#     "ReturnCodeInResponse": true
#   }
# }
```

### Issue: Docker container returning code in production

**Cause:** Environment variable override or wrong environment

**Solution:**
```dockerfile
# In Dockerfile, ensure:
ENV ASPNETCORE_ENVIRONMENT=Production

# Do NOT set:
# ENV Auth__ReturnCodeInResponse=true
```

## Best Practices

### ? DO
- Use environment-specific configuration files
- Set `ASPNETCORE_ENVIRONMENT=Production` in production
- Keep `ReturnCodeInResponse=false` in production
- Use `ReturnCodeInResponse=true` only in Development

### ? DON'T
- Don't return codes in production for security
- Don't use `#if DEBUG` for runtime configuration
- Don't hardcode sensitive behavior
- Don't enable code return via environment variables in production

## Migration Guide

### If You Previously Had:

```json
// appsettings.json
{
  "Auth": {
    "ReturnCodeInResponse": true
  }
}
```

### Change To:

```json
// appsettings.json
{
  // Remove Auth section entirely
}

// appsettings.Development.json
{
  "Auth": {
    "ReturnCodeInResponse": true
  }
}

// appsettings.Production.json
{
  "Auth": {
    "ReturnCodeInResponse": false
  }
}
```

## Summary

| Environment | Configuration File | Returns Code? |
|-------------|-------------------|---------------|
| **Development** | `appsettings.Development.json` | ? Yes |
| **Staging** | `appsettings.Staging.json` | Optional |
| **Production** | `appsettings.Production.json` | ? No |
| **Docker (default)** | `appsettings.Production.json` | ? No |

## Verification Checklist

After deployment, verify:

- [ ] AWS/Docker returns only `{"sent": true}`
- [ ] Localhost Development returns code and authSessionId
- [ ] `ASPNETCORE_ENVIRONMENT` is set to `Production` in Docker
- [ ] No environment variable overrides in production
- [ ] `appsettings.Production.json` exists with `ReturnCodeInResponse: false`
- [ ] `appsettings.Development.json` exists with `ReturnCodeInResponse: true`

---

**Status:** ? Implemented and Tested
**Security:** ? Production-safe
**Flexibility:** ? Environment-specific control
