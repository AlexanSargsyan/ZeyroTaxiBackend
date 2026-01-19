namespace Taxi_API.DTOs
{
    /// <summary>
    /// Request model for bulk driver profile submission with all required documents
    /// </summary>
    public class DriverProfileSubmissionRequest
    {
        // Passport photos
        public IFormFile? PassportFront { get; set; }
        public IFormFile? PassportBack { get; set; }

        // Driver license photos
        public IFormFile? DlFront { get; set; }
        public IFormFile? DlBack { get; set; }

        // Car exterior photos
        public IFormFile? CarFront { get; set; }
        public IFormFile? CarBack { get; set; }
        public IFormFile? CarLeft { get; set; }
        public IFormFile? CarRight { get; set; }

        // Car interior photo
        public IFormFile? CarInterior { get; set; }

        // Technical passport (car registration)
        public IFormFile? TechPassport { get; set; }
    }
}
