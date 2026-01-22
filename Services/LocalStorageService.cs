using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Taxi_API.Services
{
    public class LocalStorageService : IStorageService
    {
        private readonly string _root;
        private readonly ILogger<LocalStorageService> _logger;

        public LocalStorageService(IConfiguration config, ILogger<LocalStorageService> logger)
        {
            _root = config["Storage:Path"] ?? "Storage";
            _logger = logger;
            
            try
            {
                if (!Directory.Exists(_root))
                {
                    _logger.LogInformation("Creating storage directory: {Path}", _root);
                    Directory.CreateDirectory(_root);
                }
                _logger.LogInformation("Storage directory ready: {Path}", _root);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create storage directory: {Path}", _root);
                throw;
            }
        }

        public async Task<string> SaveFileAsync(Stream stream, string fileName)
        {
            try
            {
                var safe = Path.GetRandomFileName();
                var full = Path.Combine(_root, safe + Path.GetExtension(fileName));
                
                _logger.LogInformation("Saving file: {FileName} to {Path}", fileName, full);
                
                using var fs = File.Create(full);
                await stream.CopyToAsync(fs);
                
                _logger.LogInformation("File saved successfully: {Path}, Size: {Size} bytes", full, fs.Length);
                return full;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save file: {FileName}", fileName);
                throw;
            }
        }

        public Task DeleteFileAsync(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogInformation("File deleted: {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file: {Path}", path);
            }
            return Task.CompletedTask;
        }
    }
}