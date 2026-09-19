using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Hoonsoo;

public readonly record struct OcrWordInfo(string Text, Rectangle BoundingBox);

public readonly record struct OcrLineInfo(string Text, Rectangle BoundingBox, IReadOnlyList<OcrWordInfo> Words);

public sealed class OcrPageResult(string text, IReadOnlyList<OcrLineInfo> lines, int imageWidth, int imageHeight, bool isBlackScreen = false)
{
    public bool IsBlackScreen { get; } = isBlackScreen;
    public string Text { get; } = text;
    public IReadOnlyList<OcrLineInfo> Lines { get; } = lines;
    public int ImageWidth { get; } = imageWidth;
    public int ImageHeight { get; } = imageHeight;
}

public static class WindowsMediaOcr
{
    private static readonly Lazy<OcrEngine?> EngineInstance = new(() =>
    {
        try
        {
            if (OcrEngine.AvailableRecognizerLanguages.Count == 0) return null;
            // Prefer English if installed, else fallback to user profile language (e.g. Korean engine which recognizes Latin/English)
            var english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (english is not null) return OcrEngine.TryCreateFromLanguage(english);
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
        }
        catch
        {
            return null;
        }
    });

    public static bool IsAvailable => EngineInstance.Value is not null;

    public static async Task<OcrPageResult?> RecognizeAsync(Bitmap bitmap, int offsetX = 0, int offsetY = 0, CancellationToken token = default)
    {
        var engine = EngineInstance.Value;
        if (engine is null || bitmap.Width < 8 || bitmap.Height < 8) return null;

        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        SoftwareBitmap softwareBmp;
        try
        {
            int bytes = Math.Abs(data.Stride) * bitmap.Height;
            byte[] rawBytes = new byte[bytes];
            Marshal.Copy(data.Scan0, rawBytes, 0, bytes);
            softwareBmp = SoftwareBitmap.CreateCopyFromBuffer(rawBytes.AsBuffer(), BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Ignore);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        token.ThrowIfCancellationRequested();
        var ocrResult = await engine.RecognizeAsync(softwareBmp).AsTask(token);
        if (ocrResult is null) return null;

        var lineList = new List<OcrLineInfo>(ocrResult.Lines.Count);
        foreach (var line in ocrResult.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            var wordList = new List<OcrWordInfo>(line.Words.Count);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                int wx = (int)Math.Round(r.X) + offsetX;
                int wy = (int)Math.Round(r.Y) + offsetY;
                int ww = Math.Max(1, (int)Math.Round(r.Width));
                int wh = Math.Max(1, (int)Math.Round(r.Height));

                wordList.Add(new OcrWordInfo(word.Text, new Rectangle(wx, wy, ww, wh)));
                minX = Math.Min(minX, wx);
                minY = Math.Min(minY, wy);
                maxX = Math.Max(maxX, wx + ww);
                maxY = Math.Max(maxY, wy + wh);
            }

            var lineRect = wordList.Count > 0
                ? new Rectangle(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY))
                : new Rectangle(offsetX, offsetY, 1, 1);

            lineList.Add(new OcrLineInfo(line.Text, lineRect, wordList));
        }

        return new OcrPageResult(ocrResult.Text, lineList, bitmap.Width, bitmap.Height);
    }

    public static bool IsBitmapBlackScreen(Bitmap bmp)
    {
        if (bmp.Width < 8 || bmp.Height < 8) return false;
        int sampleCount = 0;
        int blackCount = 0;
        int stepX = Math.Max(1, bmp.Width / 16);
        int stepY = Math.Max(1, bmp.Height / 16);
        for (int x = stepX / 2; x < bmp.Width; x += stepX)
        {
            for (int y = stepY / 2; y < bmp.Height; y += stepY)
            {
                sampleCount++;
                var c = bmp.GetPixel(x, y);
                if (c.R <= 3 && c.G <= 3 && c.B <= 3) blackCount++;
            }
        }
        return sampleCount >= 16 && (blackCount / (double)sampleCount) >= 0.98;
    }

    public static async Task<OcrPageResult?> CaptureAndRecognizeAsync(Rectangle screenBounds, CancellationToken token = default)
    {
        if (screenBounds.Width < 8 || screenBounds.Height < 8) return null;
        using var bmp = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(screenBounds.Location, System.Drawing.Point.Empty, screenBounds.Size);
        }
        bool black = IsBitmapBlackScreen(bmp);
        var result = await RecognizeAsync(bmp, screenBounds.X, screenBounds.Y, token);
        if (result is null)
        {
            return black ? new OcrPageResult("", [], screenBounds.Width, screenBounds.Height, isBlackScreen: true) : null;
        }
        return new OcrPageResult(result.Text, result.Lines, result.ImageWidth, result.ImageHeight, isBlackScreen: black);
    }
}
