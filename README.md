# Zeyro Taxi API

Overview
--------
This is the backend API for the Zeyro taxi application (ASP.NET Core / .NET 8). It provides endpoints for user and driver authentication, voice-based chat/commands (OpenAI), order creation and lifecycle management (estimate, request, accept, cancel, complete, rate).

Quick start
-----------
Prerequisites:
- .NET 8 SDK
- SQLite (optional, the project uses a file `taxi.db` by default)

Run locally:
1. Set environment variables as needed (examples below).
2. Build and run:
   - dotnet restore
   - dotnet build
   - dotnet run --project TaxiApi.csproj

The API binds to port 5000 inside the container / process. Open the Swagger UI at:
- http://localhost:5000/swagger
- https://api.zeyro.space/swagger

Configuration
-------------
The app reads configuration from appsettings and environment variables. Useful settings:
- OpenAI:ApiKey
- Jwt:Key (fallback default is present but replace for production)
- Jwt:Issuer (default: TaxiApi)
- ConnectionStrings:Default (defaults to `Data Source=taxi.db`)

Example environment variables (PowerShell):
$env:OpenAI__ApiKey = "sk-..."
$env:Jwt__Key = "your_jwt_secret"
$env:Jwt__Issuer = "TaxiApi"
$env:ConnectionStrings__Default = "Data Source=taxi.db"

Main endpoints (summary)
------------------------
Authentication (client):
- POST /api/auth/request-code  { phone, name? } ? returns AuthSessionId
- POST /api/auth/resend        { authSessionId? , phone? } ? resend or create session
- POST /api/auth/auth          { authSessionId, code, name? } ? verify and return token
- GET  /api/auth/session/{id}  ? session status

Driver authentication (separate):
- POST /api/driver/request-code
- POST /api/driver/auth
- POST /api/driver/verify     ? verify phone and mark PhoneVerified
- POST /api/driver/login
- POST /api/driver/logout

Orders:
- POST /api/orders/estimate   ? requires coordinates (PickupLat, PickupLng, DestLat, DestLng)
- POST /api/orders/request    ? create order and start searching for driver
- POST /api/orders/accept/{id} ? set pickup coordinates, stops[], payment, pet, child, tariff
- POST /api/orders/cancel/{id}
- POST /api/orders/driver/accept/{id}
- POST /api/orders/complete/{id}
- POST /api/orders/rate/{id}  ? accepts rating (only by order owner after completion)

Voice chat / commands (OpenAI):
- POST /api/voice/upload (authorized)
  - form-data: file (audio), lang ("en","ru","hy"), audio (bool to request TTS reply)
  - Transcribes audio, detects intent (taxi/delivery/schedule) in English, Armenian, Russian and returns chat reply. If intent is taxi/delivery/schedule the model is prompted to include a short JSON the server attempts to parse and create an Order.

OpenAI and TTS
--------------
- The project uses a minimal OpenAI REST-based service. Configure `OpenAI:ApiKey`.
- TTS endpoint is called at `https://api.openai.com/v1/audio/speech` and returns WAV bytes when requested. Voice selection is configured per language (default: `alloy`).

Database
--------
- Uses EF Core with SQLite by default. The database file is `taxi.db` in the working directory.
- On startup the app calls `EnsureCreated()` which will create the database and tables automatically for simple testing. For production use EF Migrations instead.

Notes and next steps
--------------------
- SMS sending is stubbed using `IEmailService.SendAsync(phone + "@example.com"...)`. Replace with a real SMS provider (Twilio, AWS SNS) for production.
- Driver assignment and notifications are simulated; replace with a real matching algorithm and notification (push, SMS, websocket) later.
- Consider using a routing/directions API (Mapbox, Google Directions, OSRM) for accurate distance/ETA and fare calculation.
- Secure secrets (Jwt:Key, OpenAI key) via environment variables or secret store in production.
