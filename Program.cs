using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Taxi_API.Data;
using Taxi_API.Services;
using System.Text;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Bind for Docker / ECS
builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddControllers();

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
builder.Services.AddSingleton<IImageComparisonService, OpenCvImageComparisonService>();
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

// Register IOcrService implementation (TesseractOcrService) in DI.
builder.Services.AddSingleton<IOcrService, TesseractOcrService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? "very_secret_key_please_change";
var issuer = builder.Configuration["Jwt:Issuer"] ?? "TaxiApi";

var signingKey = new SymmetricSecurityKey(
    Encoding.UTF8.GetBytes(jwtKey)
);

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
            ValidateLifetime = true
        };
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Order operations by relative path (alphabetical) so auth appears before login
    c.OrderActionsBy(apiDesc => apiDesc.RelativePath);
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

// enable websockets
app.UseWebSockets();

// DO NOT force HTTPS inside container unless ALB listener is HTTPS
// app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Swagger — RELATIVE PATHS ONLY (no localhost)
app.UseSwagger();

app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Taxi API V1");
    c.RoutePrefix = "swagger";
});

// Root → Swagger (relative redirect)
app.MapGet("/", () => Results.Redirect("/swagger"));

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

        logger.LogInformation("Applying database migrations...");
        await db.Database.MigrateAsync();

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
