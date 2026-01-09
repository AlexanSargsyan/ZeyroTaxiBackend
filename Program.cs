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
builder.Services.AddScoped<ITokenService, JwtTokenService>();
builder.Services.AddSingleton<IImageComparisonService, OpenCvImageComparisonService>();
// Register OpenAI service for voice/chat
builder.Services.AddSingleton<IOpenAiService, OpenAiService>();

// WebSocket service
builder.Services.AddSingleton<ISocketService, WebSocketService>();

// Background processor for scheduled plans
builder.Services.AddHostedService<ScheduledPlanProcessor>();

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
builder.Services.AddSwaggerGen();

var app = builder.Build();

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

// Apply EF Core migrations (recommended) so DB schema is updated when the app starts.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        db.Database.Migrate();

        // Defensive fallback: ensure AuthSessions table exists (some migration edits may have skipped it)
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
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to apply database migrations at startup. Ensure migrations are created and the process has write access to the database file.");
        throw;
    }
}

app.Run();
