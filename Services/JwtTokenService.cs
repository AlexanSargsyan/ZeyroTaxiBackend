using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Taxi_API.Models;
using System.Security.Cryptography;

namespace Taxi_API.Services
{
    public class JwtTokenService : ITokenService
    {
        private readonly IConfiguration _config;

        public JwtTokenService(IConfiguration config)
        {
            _config = config;
        }

        public string GenerateToken(User user)
        {
            var key = _config["Jwt:Key"] ?? "very_secret_key_please_change";
            var issuer = _config["Jwt:Issuer"] ?? "TaxiApi";

            var claims = new[] {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim("phone", user.Phone),
                new Claim("isDriver", user.IsDriver.ToString())
            };

            // Ensure key is at least 256 bits by deriving SHA-256 of the configured key
            byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));

            var creds = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
            
            // Remove audience parameter to match validation settings (ValidateAudience = false)
            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: null,  // Don't set audience since we don't validate it
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: creds
            );
            
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}