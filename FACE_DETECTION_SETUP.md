# Face Detection Setup

## Issue
The driver identity verification feature uses OpenCV for face comparison, which requires the Haar Cascade classifier file.

## Quick Fix

### Option 1: Download the File (Recommended)

1. **Download the Haar Cascade file:**
   ```powershell
   # PowerShell
   Invoke-WebRequest -Uri "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml" -OutFile "haarcascade_frontalface_default.xml"
   ```

   Or manually download from:
   https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml

2. **Place the file in your project directory:**
   ```
   C:\Users\hakin\Desktop\Projects\ZeyroTaxiBackend\haarcascade_frontalface_default.xml
   ```

3. **Restart the application**

### Option 2: Use Without Face Detection (Temporary)

The application will now work **without** the face detection file:
- ? Driver identity upload endpoints will work
- ? Photos will be saved successfully
- ?? Face comparison will return a neutral "match" result
- ?? Admin should manually review driver photos

## Verification

After placing the file, check the application logs on startup:
- ? **If successful:** Face detection is enabled silently
- ?? **If missing:** You'll see: "Warning: haarcascade_frontalface_default.xml not found. Face detection disabled."

## File Locations Checked

The application searches for the file in these locations (in order):
1. Current directory
2. Executable directory
3. `data/` subdirectory in executable directory
4. Working directory
5. `data/` subdirectory in working directory

## For Production

Include the file in your Docker image:

```dockerfile
# In your Dockerfile
WORKDIR /app
COPY haarcascade_frontalface_default.xml .
```

Or download it during container build:

```dockerfile
RUN wget https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml
```

## Features Affected

### With Face Detection File:
- ? Automatic face matching between passport and driver license
- ? Confidence score calculation
- ? Email alerts for mismatches

### Without Face Detection File:
- ?? Returns default "match" result (75% confidence)
- ?? No automatic face comparison
- ?? Requires manual admin review of driver documents

## Testing

After setup, test the face detection:

```powershell
# Test driver identity upload
curl -X POST "http://localhost:5000/api/driver/identity/passport" `
  -H "Authorization: Bearer YOUR_TOKEN" `
  -F "Front=@passport_front.jpg" `
  -F "Back=@passport_back.jpg"
```

Check response for:
```json
{
  "success": true,
  "faceMatch": true,  // ? Should be true if faces detected
  "passportNumber": "...",
  ...
}
```

## Alternative: Disable Face Detection Completely

If you don't need face detection, you can create a simple no-op implementation:

```csharp
// In Services/SimpleImageComparisonService.cs
public class SimpleImageComparisonService : IImageComparisonService
{
    public Task<(double score, bool match)> CompareFacesAsync(string path1, string path2)
    {
        // Always return match for testing
        return Task.FromResult((1.0, true));
    }

    public Task<(double score, bool ok)> CheckCarDamageAsync(IEnumerable<string> paths)
    {
        // Always return OK
        return Task.FromResult((0.0, true));
    }
}
```

Then register it in `Program.cs`:
```csharp
// Replace:
builder.Services.AddSingleton<IImageComparisonService, OpenCvImageComparisonService>();

// With:
builder.Services.AddSingleton<IImageComparisonService, SimpleImageComparisonService>();
```

---

**Current Status:** ? Application works without the file (returns neutral results)

**Recommendation:** Download the file for production use to enable automatic face verification.
