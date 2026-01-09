namespace Taxi_API.Services
{
    public interface IFcmService
    {
        Task SendPushAsync(string deviceToken, string title, string body, Dictionary<string, string>? data = null);
    }
}
