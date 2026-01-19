using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Taxi_API.Services
{
    /// <summary>
    /// Cleans up multipart/form-data file upload parameters in Swagger
    /// Removes IFormFile internal properties and shows only the file upload field
    /// </summary>
    public class FileUploadOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            // Check if this is a multipart/form-data request
            if (operation.RequestBody?.Content?.ContainsKey("multipart/form-data") != true)
                return;

            var formDataContent = operation.RequestBody.Content["multipart/form-data"];
            if (formDataContent?.Schema?.Properties == null)
                return;

            // List of IFormFile internal properties to remove
            var propertiesToRemove = new List<string> 
            { 
                "ContentType", 
                "ContentDisposition", 
                "Headers", 
                "Length", 
                "Name", 
                "FileName" 
            };

            // Remove only the unwanted IFormFile internal properties
            // Keep 'file', 'Front', 'Back', etc. (actual file upload fields)
            foreach (var propName in propertiesToRemove.ToList())
            {
                if (formDataContent.Schema.Properties.ContainsKey(propName))
                {
                    formDataContent.Schema.Properties.Remove(propName);
                }
            }

            // Enhance file upload fields description
            foreach (var prop in formDataContent.Schema.Properties)
            {
                if (prop.Value.Type == "string" && prop.Value.Format == "binary")
                {
                    // This is a file upload field
                    if (context.ApiDescription.RelativePath?.Contains("voice/upload") == true)
                    {
                        prop.Value.Description = "Upload audio file (.wav, .mp3, .m4a, .ogg)";
                    }
                    else if (prop.Key.Equals("file", StringComparison.OrdinalIgnoreCase))
                    {
                        prop.Value.Description = "Upload file";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Simplifies IFormFile schema in Swagger to show only file upload field
    /// </summary>
    public class FileUploadSchemaFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            // Remove unwanted IFormFile properties from Swagger UI
            if (context.Type == typeof(IFormFile))
            {
                schema.Properties?.Clear();
                schema.Type = "string";
                schema.Format = "binary";
                schema.Description = "Upload file";
            }
        }
    }
}
