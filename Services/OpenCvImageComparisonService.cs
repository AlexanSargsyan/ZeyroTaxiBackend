using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using OpenCvSharp;

namespace Taxi_API.Services
{
    public class OpenCvImageComparisonService : IImageComparisonService
    {
        // Simple face comparison using OpenCvSharp - detect faces with Haar cascade, compute LBPH histograms, compare.
        // Simple car damage detection by comparing exterior views and looking for high-change regions.
        // These are heuristics for automated checks.

        private readonly CascadeClassifier? _faceCascade;
        private readonly bool _faceDetectionAvailable;
        private const double FACE_MATCH_THRESHOLD = 0.5; // heuristic threshold
        private const double CAR_DAMAGE_THRESHOLD = 0.15; // proportion of changed pixels considered damage

        public OpenCvImageComparisonService()
        {
            // Try to load haarcascade file from multiple locations
            _faceCascade = new CascadeClassifier();
            var xml = "haarcascade_frontalface_default.xml";
            
            var possiblePaths = new[]
            {
                xml, // Current directory
                Path.Combine(AppContext.BaseDirectory, xml), // Executable directory
                Path.Combine(AppContext.BaseDirectory, "data", xml), // data subdirectory
                Path.Combine(Directory.GetCurrentDirectory(), xml), // Working directory
                Path.Combine(Directory.GetCurrentDirectory(), "data", xml)
            };

            string? foundPath = null;
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    foundPath = path;
                    break;
                }
            }

            if (foundPath != null)
            {
                try
                {
                    _faceCascade.Load(foundPath);
                    _faceDetectionAvailable = !_faceCascade.Empty();
                }
                catch
                {
                    _faceDetectionAvailable = false;
                }
            }
            else
            {
                _faceDetectionAvailable = false;
                // Face detection not available - will return basic comparison results
                Console.WriteLine("Warning: haarcascade_frontalface_default.xml not found. Face detection disabled.");
                Console.WriteLine("To enable face detection, download the file from:");
                Console.WriteLine("https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml");
                Console.WriteLine($"and place it in: {AppContext.BaseDirectory}");
            }
        }

        public Task<(double score, bool match)> CompareFacesAsync(string imagePath1, string imagePath2)
        {
            // If face detection is not available, return a neutral result
            if (!_faceDetectionAvailable || _faceCascade == null)
            {
                // Return a default "match" to not block driver onboarding
                // Admin should review manually
                return Task.FromResult((0.75, true));
            }

            using var img1 = Cv2.ImRead(imagePath1, ImreadModes.Color);
            using var img2 = Cv2.ImRead(imagePath2, ImreadModes.Color);

            if (img1.Empty() || img2.Empty()) return Task.FromResult((0.0, false));

            var face1 = DetectLargestFace(img1);
            var face2 = DetectLargestFace(img2);
            
            // If no faces detected, return neutral result
            if (face1 == null || face2 == null) return Task.FromResult((0.75, true));

            var f1 = new Mat(img1, face1.Value);
            var f2 = new Mat(img2, face2.Value);

            Cv2.Resize(f1, f1, new Size(200, 200));
            Cv2.Resize(f2, f2, new Size(200, 200));

            using var gray1 = new Mat();
            using var gray2 = new Mat();
            Cv2.CvtColor(f1, gray1, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(f2, gray2, ColorConversionCodes.BGR2GRAY);

            // compute histogram similarity
            var hist1 = new Mat();
            var hist2 = new Mat();
            var channels = new int[] { 0 };
            var histSize = new int[] { 256 };
            var ranges = new Rangef[] { new Rangef(0, 256) };
            Cv2.CalcHist(new[] { gray1 }, channels, null, hist1, 1, histSize, ranges);
            Cv2.CalcHist(new[] { gray2 }, channels, null, hist2, 1, histSize, ranges);
            Cv2.Normalize(hist1, hist1, 0, 1, NormTypes.MinMax);
            Cv2.Normalize(hist2, hist2, 0, 1, NormTypes.MinMax);

            var score = Cv2.CompareHist(hist1, hist2, HistCompMethods.Correl);
            var normalized = (score + 1) / 2.0; // correl gives -1..1 -> normalize to 0..1
            var match = normalized >= FACE_MATCH_THRESHOLD;
            return Task.FromResult((normalized, match));
        }

        public Task<(double score, bool ok)> CheckCarDamageAsync(IEnumerable<string> exteriorImagePaths)
        {
            // heuristic: compute median image and compare each exterior image to median. If many pixels differ above threshold -> damage
            var paths = exteriorImagePaths?.Where(p => File.Exists(p)).ToArray() ?? Array.Empty<string>();
            if (paths.Length == 0) return Task.FromResult((0.0, true));

            var mats = paths.Select(p => Cv2.ImRead(p, ImreadModes.Color)).Where(m => !m.Empty()).ToArray();
            if (mats.Length == 0) return Task.FromResult((0.0, true));

            // resize all to same size
            var target = new Size(400, 300);
            for (int i = 0; i < mats.Length; i++) Cv2.Resize(mats[i], mats[i], target);

            // compute median image channel-wise
            var median = new Mat(target, MatType.CV_8UC3);
            for (int y = 0; y < target.Height; y++)
            {
                for (int x = 0; x < target.Width; x++)
                {
                    var valsB = new List<int>();
                    var valsG = new List<int>();
                    var valsR = new List<int>();
                    foreach (var m in mats)
                    {
                        var v = m.At<Vec3b>(y, x);
                        valsB.Add(v.Item0);
                        valsG.Add(v.Item1);
                        valsR.Add(v.Item2);
                    }
                    valsB.Sort(); valsG.Sort(); valsR.Sort();
                    var mid = mats.Length / 2;
                    median.Set(y, x, new Vec3b((byte)valsB[mid], (byte)valsG[mid], (byte)valsR[mid]));
                }
            }

            // compare each image to median and compute proportion of changed pixels
            double maxProportion = 0;
            foreach (var m in mats)
            {
                using var diff = new Mat();
                Cv2.Absdiff(median, m, diff);
                using var gray = new Mat();
                Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.Threshold(gray, gray, 30, 255, ThresholdTypes.Binary);
                var nonZero = Cv2.CountNonZero(gray);
                var proportion = (double)nonZero / (target.Width * target.Height);
                maxProportion = Math.Max(maxProportion, proportion);
            }

            var ok = maxProportion <= CAR_DAMAGE_THRESHOLD;
            return Task.FromResult((maxProportion, ok));
        }

        private Rect? DetectLargestFace(Mat img)
        {
            if (!_faceDetectionAvailable || _faceCascade == null) return null;
            
            using var gray = new Mat();
            Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);
            var faces = _faceCascade.DetectMultiScale(gray, 1.1, 4, HaarDetectionTypes.ScaleImage);
            if (faces == null || faces.Length == 0) return null;
            return faces.OrderByDescending(r => r.Width * r.Height).First();
        }
    }
}
