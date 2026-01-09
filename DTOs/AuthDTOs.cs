namespace Taxi_API.DTOs
{
    public record AuthRequest(string? AuthSessionId, string Code, string? Phone, string? Name);
    public record RequestCodeRequest(string Phone, string? Name);
    public record ResendRequest(string? Phone);
    public record VerifyRequest(string Phone, string Code, string? Name);
    public record AuthResponse(string Token, string AuthSessionId);
    public record DriverAuthResponse(string? Token, string AuthSessionId, bool Registered);
    public record VerifyResponse(string AuthSessionId);
    public record AuthTokenRequest(string Token);
}