using System.IO;
using System.Threading.Tasks;

namespace Taxi_API.Services
{
    public interface IOpenAiService
    {
        Task<string?> TranscribeAsync(Stream audioStream, string language);
        Task<string?> ChatAsync(string prompt, string language);
        Task<byte[]?> SynthesizeSpeechAsync(string text, string language, string? voice = null);
    }
}
