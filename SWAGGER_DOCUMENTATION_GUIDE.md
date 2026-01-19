# Swagger API Documentation - Complete Guide

## Overview

The Zeyro Taxi API now includes comprehensive Swagger/OpenAPI documentation with:

? **JWT Authentication Support**  
? **Organized Endpoint Categories**  
? **File Upload Support**  
? **Request/Response Examples**  
? **Interactive API Testing**  
? **Custom Styling**  

## Accessing Swagger UI

### Local Development
```
http://localhost:5000/swagger
```

### Production
```
http://zeyro.space/swagger
http://my-public-api-alb-1517396588.us-east-1.elb.amazonaws.com/swagger
```

## API Endpoint Categories

The API endpoints are organized into the following categories:

### 01. User Authentication
- `POST /api/auth/request-code` - Request SMS verification code
- `POST /api/auth/resend` - Resend verification code
- `POST /api/auth/verify` - Verify SMS code
- `POST /api/auth/auth` - Get JWT token
- `POST /api/auth/logout` - Logout and invalidate sessions
- `GET /api/auth/config-check` - Check configuration (diagnostic)

### 02. Driver Authentication
- `POST /api/driver/request-code` - Request SMS verification code (driver)
- `POST /api/driver/resend` - Resend verification code (driver)
- `POST /api/driver/verify` - Verify SMS code and mark as driver
- `POST /api/driver/auth` - Get JWT token (driver)
- `POST /api/driver/login` - Refresh JWT token
- `POST /api/driver/logout` - Logout driver

### 03. Driver Identity & Documents
- `GET /api/driver/identity` - Get all identity information
- `POST /api/driver/identity/passport` - Upload passport images (with OCR)
- `POST /api/driver/identity/license` - Upload driver license (with OCR)
- `POST /api/driver/identity/car-registration` - Upload car registration (with OCR)

### 04. Driver Profile & Management
- `POST /api/driver/submit` - Submit driver profile with all documents
- `GET /api/driver/status` - Get driver approval status
- `GET /api/driver/profile` - Get complete driver profile
- `GET /api/driver/car` - Get car information
- `PATCH /api/driver/location` - Update driver location
- `POST /api/driver/stripe/onboard` - Create Stripe onboarding

### 05. Orders & Trips
- `GET /api/orders/estimate` - Get ride estimate (query params)
- `POST /api/orders/estimate/body` - Get ride estimate (body)
- `POST /api/orders/request` - Request a new order
- `POST /api/orders/accept/{id}` - Accept order with details
- `POST /api/orders/driver/accept/{id}` - Driver accepts order
- `POST /api/orders/complete/{id}` - Complete order
- `POST /api/orders/cancel/{id}` - Cancel order
- `POST /api/orders/rate/{id}` - Rate completed order
- `GET /api/orders/trips` - Get user/driver trips (paginated)
- `GET /api/orders/reviews` - Get reviews (paginated)
- `POST /api/orders/location/{orderId}` - Update order location
- `POST /api/orders/map/receive/{id}` - Driver receives order (map)
- `POST /api/orders/map/arrive/{id}` - Driver arrives (map)
- `POST /api/orders/map/start/{id}` - Start trip (map)
- `POST /api/orders/map/complete/{id}` - Complete trip (map)
- `POST /api/orders/map/cancel/{id}` - Cancel trip (map)

### 06. Payments (Stripe)
- `POST /api/payments/create-payment-intent` - Create Stripe payment intent
- `POST /api/payments/confirm-payment` - Confirm payment
- `GET /api/payments/status/{paymentIntentId}` - Get payment status

### 07. Payments (Idram)
- `POST /api/idram/create-payment` - Create Idram payment
- `GET /api/idram/status/{billNo}` - Get Idram payment status
- `POST /api/idram/result` - Idram callback (server-to-server)

### 08. Payments (IPay)
- `POST /api/ipay/create-payment` - Create IPay payment
- `GET /api/ipay/status/{orderId}` - Get IPay payment status
- `GET /api/ipay/return` - IPay return URL (user redirect)
- `POST /api/ipay/reverse/{orderId}` - Reverse (cancel) payment
- `POST /api/ipay/refund` - Refund payment

### 09. Voice AI & Chat
- `POST /api/voice/upload` - Upload audio for transcription & AI response
- `POST /api/voice/translate` - Translate text (with optional TTS)
- `POST /api/voice/chat` - Text chat with AI (with optional TTS)

### 10. Scheduled Rides
- `POST /api/schedule/plans` - Create scheduled plan
- `GET /api/schedule/plans` - Get all scheduled plans
- `GET /api/schedule/plans/{id}` - Get specific plan
- `PUT /api/schedule/plans/{id}` - Update scheduled plan
- `DELETE /api/schedule/plans/{id}` - Delete scheduled plan
- `GET /api/schedule/plans/{id}/executions` - Get plan execution history

## Using JWT Authentication in Swagger

### Step 1: Obtain a Token

1. **For Users:**
   - Click on `POST /api/auth/request-code`
   - Enter phone number and click "Execute"
   - Copy the `code` from response
   - Click on `POST /api/auth/verify`
   - Enter phone, code, and name, then click "Execute"
   - Copy the `authSessionId` from response
   - Click on `POST /api/auth/auth`
   - Enter `authSessionId` and `code`, then click "Execute"
   - Copy the `token` from response

2. **For Drivers:**
   - Follow the same steps but use endpoints under "02. Driver Authentication"
   - Use `/api/driver/request-code`, `/api/driver/verify`, `/api/driver/auth`

### Step 2: Authorize Swagger

1. Click the **"Authorize"** button at the top right (lock icon)
2. In the "Value" field, enter: `Bearer YOUR_TOKEN_HERE`
   - Example: `Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...`
3. Click **"Authorize"**
4. Click **"Close"**

### Step 3: Test Protected Endpoints

Now all protected endpoints (marked with a lock icon ??) will include your token automatically.

## Testing File Upload Endpoints

### Driver Identity Documents

The following endpoints support file uploads in Swagger:

#### Upload Passport
1. Navigate to `POST /api/driver/identity/passport`
2. Click **"Try it out"**
3. Click **"Choose File"** for `front` parameter
4. Select front image of passport
5. Click **"Choose File"** for `back` parameter
6. Select back image of passport
7. Click **"Execute"**
8. View the OCR-extracted information in the response

#### Upload Driver License
1. Navigate to `POST /api/driver/identity/license`
2. Click **"Try it out"**
3. Upload front and back images
4. Click **"Execute"**

#### Upload Car Registration
1. Navigate to `POST /api/driver/identity/car-registration`
2. Click **"Try it out"**
3. Upload front and back images
4. Click **"Execute"**

### Voice Upload
1. Navigate to `POST /api/voice/upload`
2. Click **"Try it out"**
3. Upload audio file (WAV, MP3, etc.)
4. Set parameters:
   - `lang`: Language code (en, ru, hy)
   - `audio`: true/false (for TTS response)
5. Click **"Execute"**

## Request Examples

### User Authentication Flow

```json
// 1. Request Code
POST /api/auth/request-code
{
  "phone": "+37412345678",
  "name": "John Doe"
}

// Response:
{
  "sent": true,
  "code": "123456"
}

// 2. Verify Code
POST /api/auth/verify
{
  "phone": "+37412345678",
  "code": "123456",
  "name": "John Doe"
}

// Response:
{
  "authSessionId": "550e8400-e29b-41d4-a716-446655440000"
}

// 3. Get Token
POST /api/auth/auth
{
  "authSessionId": "550e8400-e29b-41d4-a716-446655440000",
  "code": "123456"
}

// Response:
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "authSessionId": "550e8400-e29b-41d4-a716-446655440000"
}
```

### Create Order

```json
POST /api/orders/request
Authorization: Bearer {token}

{
  "action": "taxi",
  "pickup": "Yerevan, Republic Square",
  "destination": "Yerevan Airport",
  "pickupLat": 40.1777,
  "pickupLng": 44.5126,
  "destLat": 40.1473,
  "destLng": 44.3959,
  "vehicleType": "car",
  "paymentMethod": "card",
  "petAllowed": false,
  "childSeat": false,
  "tariff": "standard"
}
```

### Get Order Estimate

```
GET /api/orders/estimate?pickupLat=40.1777&pickupLng=44.5126&destLat=40.1473&destLng=44.3959&vehicleType=car
Authorization: Bearer {token}

Response:
{
  "distanceKm": 12.5,
  "price": 2500,
  "etaMinutes": 25,
  "vehicleType": "car"
}
```

## Response Status Codes

### Success Codes
- **200 OK** - Request successful
- **201 Created** - Resource created successfully

### Client Error Codes
- **400 Bad Request** - Invalid request data
- **401 Unauthorized** - Missing or invalid token
- **403 Forbidden** - Insufficient permissions
- **404 Not Found** - Resource not found

### Server Error Codes
- **500 Internal Server Error** - Server-side error
- **502 Bad Gateway** - External service error (Idram, IPay, etc.)

## Common Request Parameters

### Pagination Parameters
- `page` (integer, default: 1) - Page number
- `pageSize` (integer, default: 20) - Items per page

### Filter Parameters
- `status` (string) - Filter by status (pending, assigned, completed, cancelled)
- `asDriver` (boolean) - View as driver (for trips endpoint)
- `minRating` (integer) - Minimum rating filter

### Location Parameters
- `lat` (double) - Latitude coordinate
- `lng` (double) - Longitude coordinate
- `pickupLat` (double) - Pickup latitude
- `pickupLng` (double) - Pickup longitude
- `destLat` (double) - Destination latitude
- `destLng` (double) - Destination longitude

## WebSocket Connection

While WebSocket connections cannot be tested through Swagger, the documentation includes information about the WebSocket endpoint:

```
ws://localhost:5000/ws?userId={guid}&role=driver|user
```

### Connection Parameters:
- `userId` (GUID, required) - User/Driver ID
- `role` (string, required) - Either "driver" or "user"

### Events:
- `motoFinding` / `carFinding` / `vanFinding` - Vehicle search started
- `motoFound` / `carFound` / `vanFound` - Vehicle assigned
- `arrive` - Driver arrived at pickup
- `start` - Trip started
- `complete` - Trip completed
- `cancelUser` / `cancelDriver` - Cancellation
- `driverLocation` - Real-time driver location updates

## Swagger UI Features

### Enabled Features:
? **Deep Linking** - Direct links to specific endpoints
? **Request Duration** - Shows API response times
? **Filter** - Search/filter endpoints
? **Validation** - Request/response validation
? **Model Expansion** - View request/response schemas
? **Try It Out** - Interactive API testing
? **Code Samples** - Multiple programming language examples

### Swagger Configuration Features:
- **Bearer Token Authentication** - Secure API access
- **File Upload Support** - Upload documents and media
- **Organized Categories** - Endpoints grouped by functionality
- **Custom Styling** - Professional, branded appearance
- **XML Documentation** - Comprehensive inline documentation
- **Schema Definitions** - Clear data model definitions

## Export Options

### Download OpenAPI Specification

You can download the OpenAPI/Swagger JSON specification:

```
http://localhost:5000/swagger/v1/swagger.json
```

Use this specification with:
- **Postman** - Import as OpenAPI collection
- **Insomnia** - Import as OpenAPI spec
- **Code Generators** - Generate client SDKs (swagger-codegen, openapi-generator)
- **API Documentation Tools** - ReDoc, Stoplight, etc.

## Generating Client SDKs

### Using Swagger Codegen

```bash
# Install swagger-codegen
npm install -g @openapitools/openapi-generator-cli

# Generate Dart/Flutter client
openapi-generator-cli generate -i http://localhost:5000/swagger/v1/swagger.json -g dart -o ./flutter-client

# Generate JavaScript/TypeScript client
openapi-generator-cli generate -i http://localhost:5000/swagger/v1/swagger.json -g typescript-axios -o ./ts-client

# Generate C# client
openapi-generator-cli generate -i http://localhost:5000/swagger/v1/swagger.json -g csharp -o ./csharp-client

# Generate Python client
openapi-generator-cli generate -i http://localhost:5000/swagger/v1/swagger.json -g python -o ./python-client
```

## Best Practices

### 1. Always Authenticate First
Before testing protected endpoints, obtain a token and authorize Swagger.

### 2. Use Correct Content-Type
- JSON endpoints: `application/json`
- File uploads: `multipart/form-data`
- Form data: `application/x-www-form-urlencoded`

### 3. Check Response Status
- Green (2xx): Success
- Orange (4xx): Client error - check your request
- Red (5xx): Server error - check logs

### 4. Read Error Messages
Error responses include descriptive messages to help debug issues.

### 5. Test in Order
Follow logical flows:
1. Authentication
2. Profile setup
3. Order creation
4. Order lifecycle
5. Payment
6. Completion

## Troubleshooting

### "401 Unauthorized"
- **Cause**: Missing or invalid token
- **Solution**: Click "Authorize" and enter valid Bearer token

### "400 Bad Request"
- **Cause**: Invalid request data
- **Solution**: Check required fields and data formats

### "404 Not Found"
- **Cause**: Resource doesn't exist or wrong ID
- **Solution**: Verify resource ID and endpoint path

### "502 Bad Gateway"
- **Cause**: External service (Idram, IPay, Stripe) error
- **Solution**: Check external service status and configuration

### File Upload Not Working
- **Cause**: Wrong content type or file format
- **Solution**: Use multipart/form-data, check file size (<10MB)

## Security Considerations

### Production Deployment
1. ? Use HTTPS only (configure ALB/reverse proxy)
2. ? Rotate JWT secret keys regularly
3. ? Implement rate limiting
4. ? Monitor API usage
5. ? Enable request logging
6. ? Restrict Swagger access (if needed)

### API Keys
Store sensitive keys in environment variables or secret management:
- `Jwt:Key` - JWT signing key
- `OpenAI:ApiKey` - OpenAI API key
- `Stripe:SecretKey` - Stripe secret key
- `Idram:SecretKey` - Idram secret key
- `IPay:Password` - IPay password

## Custom Styling

The Swagger UI includes custom CSS styling with:
- **Brand Colors** - Green primary, blue secondary
- **Improved Readability** - Better fonts and spacing
- **Color-Coded Methods** - POST (green), GET (blue), etc.
- **Enhanced Buttons** - Styled execute, try-it-out buttons
- **Responsive Design** - Works on mobile devices
- **Custom Scrollbars** - Branded scrollbar colors

## Additional Resources

- **API Documentation**: See endpoint-specific markdown files
- **Authentication Guide**: `DRIVER_AUTHENTICATION_GUIDE.md`
- **Payment Integration**: `IDRAM_TESTING.md`
- **Driver Identity**: `DRIVER_IDENTITY_IMAGE_UPLOAD.md`

## Support

For API questions or issues:
- **Email**: support@zeyro.space
- **Documentation**: http://zeyro.space/swagger
- **GitHub**: Check repository issues

---

**Last Updated**: January 2026  
**API Version**: 1.0  
**Status**: ? Production Ready
