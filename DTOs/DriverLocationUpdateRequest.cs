namespace Taxi_API.DTOs
{
    /// <summary>
    /// Request model for updating driver's current location
    /// </summary>
    public class DriverLocationUpdateRequest
    {
        /// <summary>
        /// Latitude coordinate
        /// </summary>
        /// <example>40.7128</example>
        public double Lat { get; set; }

        /// <summary>
        /// Longitude coordinate
        /// </summary>
        /// <example>-74.0060</example>
        public double Lng { get; set; }
    }
}
