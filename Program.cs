using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Taxi_API.Data;
using Taxi_API.Services;
using System.Text;
using Microsoft.OpenApi.Models;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Bind for Docker / ECS
builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddControllers();

// Add CORS policy
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("Default")
        ?? "Data Source=taxi.db"
    )
);

builder.Services.AddScoped<IStorageService, LocalStorageService>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<ISmsService, VeloconnectSmsService>();
builder.Services.AddScoped<ITokenService, JwtTokenService>();

// Native library services - always use real implementations
// These require native dependencies (OpenCV, Tesseract) to be installed
builder.Services.AddSingleton<IImageComparisonService, OpenCvImageComparisonService>();
builder.Services.AddSingleton<IOcrService, TesseractOcrService>();

// Register OpenAI service for voice/chat
builder.Services.AddSingleton<IOpenAiService, OpenAiService>();

// WebSocket service
builder.Services.AddSingleton<ISocketService, WebSocketService>();

// Background processor for scheduled plans
builder.Services.AddHostedService<ScheduledPlanProcessor>();
builder.Services.AddSingleton<IFcmService, FcmService>();
builder.Services.AddScoped<IPaymentService, StripePaymentService>();

// Register Idram payment service
builder.Services.AddScoped<IdramPaymentService>();

// Register IPay payment service
builder.Services.AddHttpClient();
builder.Services.AddScoped<IPayPaymentService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "very_secret_key_please_change";
var issuer = builder.Configuration["Jwt:Issuer"] ?? "TaxiApi";

// Use SHA256 to derive a consistent key (same as in JwtTokenService)
var keyBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(jwtKey));
var signingKey = new SymmetricSecurityKey(keyBytes);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Zeyro Taxi API",
        Version = "v1",
        Description = @"
# Zeyro Taxi Backend API

Complete REST API for the Zeyro Taxi platform supporting:
- **User & Driver Authentication** (SMS-based verification)
- **Order Management** (Taxi, Delivery, Scheduled rides)
- **Driver Profile Management** (Document upload with OCR)
- **Payment Integration** (Card management, Idram, IPay)
- **Voice AI** (Speech-to-text, Text-to-speech, Chat)
- **Real-time Communication** (WebSocket support)
- **Location Tracking** (GPS coordinates)

## Authentication Flow

### For Users (Clients):
1. `POST /api/auth/request-code` - Request SMS verification code
2. `POST /api/auth/verify` - Verify the code
3. `POST /api/auth/auth` - Get JWT token
4. Use token in `Authorization: Bearer {token}` header

### For Drivers:
1. `POST /api/driver/request-code` - Request SMS verification code
2. `POST /api/driver/verify` - Verify the code (marks as driver)
3. `POST /api/driver/auth` - Get JWT token
4. Use token in `Authorization: Bearer {token}` header

## WebSocket Connection
Connect to `/ws?userId={guid}&role=driver|user` for real-time updates.

## File Upload Endpoints
Several endpoints support multipart/form-data for file uploads:
- Driver profile submission (base64 encoded images)
- Driver identity documents (multipart file upload)
- Voice audio upload

## API Categories
- **User Authentication**: Client authentication and session management
- **Driver Authentication**: Driver-specific authentication
- **Driver Identity & Documents**: Document upload with OCR
- **Driver Profile & Management**: Profile management and location updates
- **Orders & Trips (Client)**: Client-side order management, estimates, and trip history
- **Orders & Trips (Driver)**: Driver-side order acceptance, location updates, and trip lifecycle
- **Payments**: Payment card management and processing
- **Payments (Idram)**: Idram payment processing
- **Payments (IPay)**: IPay payment processing
- **Voice AI & Chat**: AI-powered voice and chat features
- **Scheduled Rides**: Scheduled ride management
"
    });

    // JWT Bearer Authentication
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = @"JWT Authorization header using the Bearer scheme.
                      Enter 'Bearer' [space] and then your token in the text input below.
                      Example: 'Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                },
                Scheme = "oauth2",
                Name = "Bearer",
                In = ParameterLocation.Header
            },
            new List<string>()
        }
    });

    // Enable annotations for better documentation
    c.EnableAnnotations();

    // Order actions by controller and then by path
    c.OrderActionsBy(apiDesc => $"{apiDesc.ActionDescriptor.RouteValues["controller"]}_{apiDesc.RelativePath}");

    // Tag controllers for better organization
    c.TagActionsBy(api =>
    {
        var controllerName = api.ActionDescriptor.RouteValues["controller"];
        return controllerName switch
        {
            "Auth" => new[] { "User Authentication" },
            "Driver" when api.RelativePath?.Contains("/identity") == true => new[] { "Driver Identity & Documents" },
            "Driver" when api.HttpMethod == "POST" && (api.RelativePath?.Contains("/request-code") == true || 
                          api.RelativePath?.Contains("/verify") == true || 
                          api.RelativePath?.Contains("/auth") == true ||
                          api.RelativePath?.Contains("/login") == true ||
                          api.RelativePath?.Contains("/logout") == true) => new[] { "Driver Authentication" },
            "Driver" => new[] { "Driver Profile & Management" },
            "Orders" when api.RelativePath?.Contains("/accept-order") == true ||
                         api.RelativePath?.Contains("/driver/") == true ||
                         api.RelativePath?.Contains("/location/") == true ||
                         api.RelativePath?.Contains("/map/") == true ||
                         api.RelativePath?.Contains("/complete/") == true => new[] { "Orders & Trips (Driver)" },
            "Orders" => new[] { "Orders & Trips (Client)" },
            "Payments" => new[] { "Payments" },
            "Idram" => new[] { "Payments (Idram)" },
            "IPay" => new[] { "Payments (IPay)" },
            "Voice" => new[] { "Voice AI & Chat" },
            "Schedule" => new[] { "Scheduled Rides" },
            _ => new[] { controllerName ?? "Other" }
        };
    });

    // Include XML comments if available
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }

    // Custom schema IDs to avoid conflicts
    c.CustomSchemaIds(type => type.FullName);

    // Simplify IFormFile schema - show only as file upload
    c.MapType<IFormFile>(() => new OpenApiSchema
    {
        Type = "string",
        Format = "binary",
        Description = "Upload file"
    });
    
    // Use filters to clean up file upload parameters
    c.SchemaFilter<FileUploadSchemaFilter>();
    c.OperationFilter<FileUploadOperationFilter>();
});

var app = builder.Build();

// Run migrations before hosted services start
await EnsureDatabaseMigratedAsync(app.Services, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program"));

// REQUIRED for ALB / reverse proxy
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost
});

// Enable static files for Swagger custom CSS
app.UseStaticFiles();

// Enable CORS
app.UseCors();

// enable websockets
app.UseWebSockets();

// DO NOT force HTTPS inside container unless ALB listener is HTTPS
// app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Enable Swagger in all environments for easier testing
app.UseSwagger(c =>
{
    c.RouteTemplate = "swagger/{documentName}/swagger.json";
});

app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Zeyro Taxi API V1");
    c.RoutePrefix = "swagger";
    c.DocumentTitle = "Zeyro Taxi API Documentation";
    c.DefaultModelsExpandDepth(2);
    c.DefaultModelRendering(Swashbuckle.AspNetCore.SwaggerUI.ModelRendering.Model);
    c.DisplayRequestDuration();
    c.EnableDeepLinking();
    c.EnableFilter();
    c.ShowExtensions();
    c.EnableValidator();
    c.SupportedSubmitMethods(Swashbuckle.AspNetCore.SwaggerUI.SubmitMethod.Get, 
                             Swashbuckle.AspNetCore.SwaggerUI.SubmitMethod.Post,
                             Swashbuckle.AspNetCore.SwaggerUI.SubmitMethod.Put,
                             Swashbuckle.AspNetCore.SwaggerUI.SubmitMethod.Delete,
                             Swashbuckle.AspNetCore.SwaggerUI.SubmitMethod.Patch);
    
    // Custom CSS for better UI
    c.InjectStylesheet("/swagger-ui/custom.css");
});

// Root → Swagger (relative redirect)
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.UseAuthentication();
app.UseAuthorization();

// WebSocket endpoint at /ws?userId={guid}&role=driver|user
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket requests only");
        return;
    }

    var userIdStr = context.Request.Query["userId"].ToString();
    var role = context.Request.Query["role"].ToString();
    if (!Guid.TryParse(userIdStr, out var userId))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("userId query parameter required and must be a GUID");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var wsService = context.RequestServices.GetRequiredService<ISocketService>();
    await wsService.HandleRawSocketAsync(userId, role, socket);
});

app.MapControllers();

app.Run();

// Helper to ensure DB schema exists and apply migrations; runs before hosted services start
static async Task EnsureDatabaseMigratedAsync(IServiceProvider services, ILogger logger)
{
    try
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        logger.LogInformation("Checking database state...");
        
        // Check if database exists
        var canConnect = await db.Database.CanConnectAsync();
        
        if (!canConnect)
        {
            logger.LogInformation("Database does not exist. Creating and applying migrations...");
            await db.Database.MigrateAsync();
        }
        else
        {
            // Database exists, check if migrations are needed
            var pendingMigrations = await db.Database.GetPendingMigrationsAsync();
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
            
            logger.LogInformation($"Applied migrations: {string.Join(", ", appliedMigrations)}");
            logger.LogInformation($"Pending migrations: {string.Join(", ", pendingMigrations)}");
            
            if (pendingMigrations.Any())
            {
                logger.LogInformation("Applying pending migrations...");
                try
                {
                    await db.Database.MigrateAsync();
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
                {
                    // Error 1 means table already exists - this can happen if the database was created manually
                    logger.LogWarning(ex, "Migration failed due to existing tables. The database schema may already be up to date.");
                    
                    // Verify the __EFMigrationsHistory table exists and has the correct entries
                    var conn = db.Database.GetDbConnection();
                    await conn.OpenAsync();
                    await using (conn)
                    {
                        // Check if __EFMigrationsHistory exists
                        using var checkCmd = conn.CreateCommand();
                        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
                        var historyExists = await checkCmd.ExecuteScalarAsync();
                        
                        if (historyExists == null || historyExists == DBNull.Value)
                        {
                            logger.LogInformation("Creating __EFMigrationsHistory table...");
                            using var createHistoryCmd = conn.CreateCommand();
                            createHistoryCmd.CommandText = @"
                                CREATE TABLE __EFMigrationsHistory (
                                    MigrationId TEXT NOT NULL PRIMARY KEY,
                                    ProductVersion TEXT NOT NULL
                                );";
                            await createHistoryCmd.ExecuteNonQueryAsync();
                        }
                        
                        // Insert migration records if they don't exist
                        var allMigrations = new[]
                        {
                            ("20260101000000_InitialCreate", "8.0.0"),
                            ("20260108095046_AddScheduledPlanTables", "8.0.0"),
                            ("20260114094508_AddIdramPayments", "8.0.0"),
                            ("20260114094924_AddIPayPayments", "8.0.0"),
                            ("20260120000000_AddCardholderNameToPaymentCards", "8.0.0")
                        };
                        
                        foreach (var (migrationId, productVersion) in allMigrations)
                        {
                            using var checkMigrationCmd = conn.CreateCommand();
                            checkMigrationCmd.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = @id";
                            var param = checkMigrationCmd.CreateParameter();
                            param.ParameterName = "@id";
                            param.Value = migrationId;
                            checkMigrationCmd.Parameters.Add(param);
                            
                            var exists = Convert.ToInt32(await checkMigrationCmd.ExecuteScalarAsync());
                            
                            if (exists == 0)
                            {
                                logger.LogInformation($"Recording migration {migrationId} in history...");
                                using var insertCmd = conn.CreateCommand();
                                insertCmd.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@id, @version)";
                                var idParam = insertCmd.CreateParameter();
                                idParam.ParameterName = "@id";
                                idParam.Value = migrationId;
                                insertCmd.Parameters.Add(idParam);
                                
                                var versionParam = insertCmd.CreateParameter();
                                versionParam.ParameterName = "@version";
                                versionParam.Value = productVersion;
                                insertCmd.Parameters.Add(versionParam);
                                
                                await insertCmd.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }
            }
            else
            {
                logger.LogInformation("Database is up to date. No migrations to apply.");
            }
        }

        // Defensive fallback: ensure AuthSessions table exists
        try
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using (conn)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='AuthSessions';";
                var res = await cmd.ExecuteScalarAsync();
                if (res == null || res == DBNull.Value)
                {
                    logger.LogInformation("AuthSessions table missing — creating it as a fallback.");
                    var createSql = @"CREATE TABLE IF NOT EXISTS AuthSessions (
                        Id TEXT PRIMARY KEY,
                        Phone TEXT NOT NULL,
                        Code TEXT NOT NULL,
                        Verified INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL
                    );";
                    using var createCmd = conn.CreateCommand();
                    createCmd.CommandText = createSql;
                    await createCmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to verify/create AuthSessions table fallback");
        }

        // Defensive: ensure Users table has PhoneVerified column
        try
        {
            var conn2 = db.Database.GetDbConnection();
            await conn2.OpenAsync();
            await using (conn2)
            {
                using var checkCmd = conn2.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='PhoneVerified';";
                var colExists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
                if (colExists == 0)
                {
                    logger.LogInformation("Users.PhoneVerified column missing — adding column.");
                    using var alterCmd = conn2.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE Users ADD COLUMN PhoneVerified INTEGER NOT NULL DEFAULT 0;";
                    await alterCmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to verify/create Users.PhoneVerified column fallback");
        }

        // Defensive: ensure DriverProfiles table has CarOk and FaceMatch columns
        try
        {
            var conn3 = db.Database.GetDbConnection();
            await conn3.OpenAsync();
            await using (conn3)
            {
                // Check and add CarOk column
                using var checkCarOkCmd = conn3.CreateCommand();
                checkCarOkCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('DriverProfiles') WHERE name='CarOk';";
                var carOkExists = Convert.ToInt32(await checkCarOkCmd.ExecuteScalarAsync());
                if (carOkExists == 0)
                {
                    logger.LogInformation("DriverProfiles.CarOk column missing — adding column.");
                    // SQLite doesn't support adding NOT NULL columns with DEFAULT in one step if table has data
                    // Add as nullable first
                    using var alterCmd1 = conn3.CreateCommand();
                    alterCmd1.CommandText = "ALTER TABLE DriverProfiles ADD COLUMN CarOk INTEGER;";
                    await alterCmd1.ExecuteNonQueryAsync();
                    
                    // Update existing rows
                    using var updateCmd1 = conn3.CreateCommand();
                    updateCmd1.CommandText = "UPDATE DriverProfiles SET CarOk = 1 WHERE CarOk IS NULL;";
                    await updateCmd1.ExecuteNonQueryAsync();
                }

                // Check and add FaceMatch column
                using var checkFaceMatchCmd = conn3.CreateCommand();
                checkFaceMatchCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('DriverProfiles') WHERE name='FaceMatch';";
                var faceMatchExists = Convert.ToInt32(await checkFaceMatchCmd.ExecuteScalarAsync());
                if (faceMatchExists == 0)
                {
                    logger.LogInformation("DriverProfiles.FaceMatch column missing — adding column.");
                    // Add as nullable first
                    using var alterCmd2 = conn3.CreateCommand();
                    alterCmd2.CommandText = "ALTER TABLE DriverProfiles ADD COLUMN FaceMatch INTEGER;";
                    await alterCmd2.ExecuteNonQueryAsync();
                    
                    // Update existing rows
                    using var updateCmd2 = conn3.CreateCommand();
                    updateCmd2.CommandText = "UPDATE DriverProfiles SET FaceMatch = 1 WHERE FaceMatch IS NULL;";
                    await updateCmd2.ExecuteNonQueryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to verify/create DriverProfiles columns fallback");
        }

        // Defensive: ensure PaymentCards table has CardholderName column
        try
        {
            var conn4 = db.Database.GetDbConnection();
            await conn4.OpenAsync();
            await using (conn4)
            {
                // Check and add CardholderName column
                using var checkCardholderCmd = conn4.CreateCommand();
                checkCardholderCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('PaymentCards') WHERE name='CardholderName';";
                var cardholderExists = Convert.ToInt32(await checkCardholderCmd.ExecuteScalarAsync());
                if (cardholderExists == 0)
                {
                    logger.LogInformation("PaymentCards.CardholderName column missing — adding column.");
                    using var alterCmd = conn4.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE PaymentCards ADD COLUMN CardholderName TEXT;";
                    await alterCmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to verify/create PaymentCards.CardholderName column fallback");
        }

        logger.LogInformation("Database migrations applied or verified");
    }
    catch (Exception ex)
    {
        var loggerFactory = services.GetService<ILoggerFactory>();
        var lg = loggerFactory?.CreateLogger("EnsureDatabaseMigratedAsync");
        lg?.LogError(ex, "Failed to apply database migrations at startup. Ensure migrations are created and the process has write access to the database file.");
        throw;
    }
}
