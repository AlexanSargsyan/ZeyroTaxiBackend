namespace Taxi_API.DTOs
{
    public record AuthRequest(string AuthSessionId, string Code, string? Name);
    public record RequestCodeRequest(string Phone, string? Name);
    public record ResendRequest(string? AuthSessionId, string? Phone);
    public record AuthResponse(string Token, string AuthSessionId);
    public record DriverAuthResponse(string? Token, string AuthSessionId, bool Registered);
}