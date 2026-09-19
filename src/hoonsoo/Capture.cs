using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Runtime.InteropServices;
using Tesseract;

namespace Hoonsoo;
public static class CaptureWorker
{
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(Native.Point point, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
    private static string Legacy(int x, int y)
    {
        Accessibility.IAccessible? accessible = null;
        try
        {
            if (AccessibleObjectFromPoint(new Native.Point(x, y), out accessible, out var child) != 0) return "";
            var value = TextQuality.Clean(accessible.get_accValue(child));
            return value.Length > 0 ? value : TextQuality.Clean(accessible.get_accName(child));
        }
        catch { return ""; }
        finally { if (accessible is not null && Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
    }
    public static CaptureResult? Uia(int x, int y)
    {
        try
        {
            var point = new System.Windows.Point(x, y);
            var element = AutomationElement.FromPoint(point);
            if (element is null || element.Current.IsPassword) return null;
            int pid = element.Current.ProcessId;
            var candidates = new List<AutomationElement> { element };
            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(element);
            for (int n = 0; child is not null && n < 12; n++, child = walker.GetNextSibling(child))
                if (child.Current.BoundingRectangle.Contains(point)) candidates.Add(child);
            var parent = element;
            for (int n = 0; n < 2; n++)
            {
                parent = walker.GetParent(parent);
                if (parent is null || parent.Current.ProcessId != pid || parent.Current.ControlType == ControlType.Window) break;
                candidates.Add(parent);
            }
            foreach (var e in candidates)
            {
                if (e.Current.IsPassword) continue;
                var texts = new List<string>();
                if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
                {
                    var pattern = (TextPattern)tp;
                    try { var range = pattern.RangeFromPoint(point); range.ExpandToEnclosingUnit(TextUnit.Paragraph); texts.Add(range.GetText(4001)); } catch { }
                    texts.Add(pattern.DocumentRange.GetText(4001));
                }
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) texts.Add(((ValuePattern)vp).Current.Value);
                texts.Add(Legacy(x, y));
                texts.Add(e.Current.Name);
                foreach (var text in texts) { var clean = TextQuality.Clean(text); if (clean.Length > 0) return new(clean, "UI Automation"); }
            }
        }
        catch { }
        return null;
    }
    public static CaptureResult? Ocr(Rectangle rectangle)
    {
        if (rectangle.Width < 8 || rectangle.Height < 8 || (long)rectangle.Width * rectangle.Height > 12000000) return null;
        using var bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rectangle.Location, System.Drawing.Point.Empty, rectangle.Size);
        return Recognize(bitmap);
    }
    public static CaptureResult? Recognize(Bitmap bitmap)
    {
        if (WindowsMediaOcr.IsAvailable)
        {
            try
            {
                var ocrTask = WindowsMediaOcr.RecognizeAsync(bitmap);
                ocrTask.Wait(4000);
                if (ocrTask.IsCompletedSuccessfully && ocrTask.Result is not null)
                {
                    var cleanText = TextQuality.Clean(ocrTask.Result.Text);
                    if (cleanText.Length > 0) return new(cleanText, "Windows Media OCR", true);
                }
            }
            catch { }
        }

        using var scaled = new Bitmap(bitmap, new Size(bitmap.Width * 2, bitmap.Height * 2));
        using var stream = new MemoryStream(); scaled.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(stream.ToArray());
        using var engine = new TesseractEngine(Path.Combine(AppContext.BaseDirectory, "tessdata"), "eng", EngineMode.LstmOnly);
        using var page = engine.Process(pix, PageSegMode.Auto);
        var text = TextQuality.Clean(page.GetText());
        return text.Length == 0 ? null : new(text, "Local OCR", page.GetMeanConfidence() >= 0.55f);
    }
    public static int Run(string[] args)
    {
        try
        {
            CaptureResult? result = args[0] == "--uia" ? Uia(int.Parse(args[1]), int.Parse(args[2])) : Ocr(new Rectangle(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4])));
            Console.Write(JsonSerializer.Serialize(result)); return 0;
        }
        catch { Console.Write("null"); return 1; }
    }
}
public sealed class CaptureClient(string? executable = null)
{
    public async Task<CaptureResult?> RunAsync(string mode, CancellationToken token, params int[] coordinates)
    {
        var info = new ProcessStartInfo(executable ?? Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(mode); foreach (int n in coordinates) info.ArgumentList.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(mode == "--uia" ? 1800 : 12000);
        try
        {
            token.ThrowIfCancellationRequested();
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token); await errors;
            return JsonSerializer.Deserialize<CaptureResult>(await output);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }
    public static Rectangle Around(Native.Point point)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).Bounds;
        var rect = Rectangle.Intersect(screen, new Rectangle(point.X - 420, point.Y - 140, 840, 280));
        var window = Native.GetAncestor(Native.WindowFromPoint(point), 2);
        if (Native.GetWindowRect(window, out var w)) rect = Rectangle.Intersect(rect, Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom));
        return rect;
    }
}
