using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Taxi_API.Services
{
    public class SmtpEmailService : IEmailService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<SmtpEmailService> _logger;

        public SmtpEmailService(IConfiguration config, ILogger<SmtpEmailService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task SendAsync(string to, string subject, string body)
        {
            var host = _config["Smtp:Host"];
            if (string.IsNullOrWhiteSpace(host))
            {
                _logger.LogInformation("Smtp host not configured, skipping email to {to}", to);
                return;
            }

            var portStr = _config["Smtp:Port"];
            int port = 587;
            if (!string.IsNullOrWhiteSpace(portStr) && !int.TryParse(portStr, out port))
            {
                _logger.LogWarning("Invalid Smtp:Port value '{portStr}', falling back to 587", portStr);
                port = 587;
            }

            var useSsl = false;
            if (bool.TryParse(_config["Smtp:UseSsl"], out var parsedSsl)) useSsl = parsedSsl;

            var username = _config["Smtp:Username"];
            var password = _config["Smtp:Password"];

            var msg = new MimeMessage();
            try
            {
                msg.From.Add(MailboxAddress.Parse(_config["Smtp:From"] ?? "no-reply@example.com"));
            }
            catch
            {
                msg.From.Add(new MailboxAddress("No Reply", "no-reply@example.com"));
            }

            try
            {
                msg.To.Add(MailboxAddress.Parse(to));
            }
            catch
            {
                _logger.LogWarning("Invalid recipient address: {to}", to);
                return;
            }

            msg.Subject = subject;
            msg.Body = new TextPart("plain") { Text = body };

            try
            {
                using var client = new SmtpClient();
                // Connect with appropriate SSL option
                var secureOption = useSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
                await client.ConnectAsync(host, port, secureOption);

                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    try
                    {
                        await client.AuthenticateAsync(username, password);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SMTP authentication failed for user {user}", username);
                    }
                }

                try
                {
                    await client.SendAsync(msg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send SMTP message to {to}", to);
                }

                try
                {
                    await client.DisconnectAsync(true);
                }
                catch { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to SMTP server {host}:{port}", host, port);
            }
        }
    }
}