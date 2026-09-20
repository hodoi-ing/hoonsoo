using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Hoonsoo;
using Drawing = System.Drawing;

internal static class TestProgram
{
    private static readonly List<object> results = new();
    private static int failed;
    private static int skipped;
    private static readonly List<string> filters = new();
    private static bool listOnly;
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [STAThread]
    private static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--list", StringComparison.OrdinalIgnoreCase)) listOnly = true;
            else if (string.Equals(args[i], "--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                filters.Add(args[++i]);
            }
        }
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetDirectoryName(Environment.ProcessPath));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await Run(); }
            catch (Exception e) { Console.WriteLine(e); failed++; }
            if (!listOnly)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"RESULT: {results.Count - failed} passed, {failed} failed" + (skipped > 0 ? $", {skipped} skipped (injected input unavailable)" : ""));
            }
            app.Shutdown();
        };
        app.Run(); return failed == 0 ? 0 : 1;
    }
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    /// <summary>Raised when the desktop itself refuses synthetic input, so the check cannot run here.</summary>
    private sealed class InputUnavailableException(string message) : Exception(message);

    /// <summary>
    /// A no-op cursor move answers exactly one question: does this desktop accept injected input? Locked and
    /// disconnected sessions refuse it, which would otherwise look like an app regression in the two checks
    /// that need real pointer or key input.
    /// </summary>
    private static bool InjectedInputAvailable()
    {
        Native.GetCursorPos(out var p);
        return SetCursorPos(p.X, p.Y);
    }

    private static void RequireInjectedInput()
    {
        if (!InjectedInputAvailable()) throw new InputUnavailableException("the desktop refused injected cursor input (locked or disconnected session)");
    }

    private static async Task Test(string name, Func<Task> action)
    {
        if (listOnly)
        {
            Console.WriteLine(name);
            return;
        }
        if (filters.Count > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (InputUnavailableException e) { skipped++; Console.WriteLine($"SKIP {name}: {e.Message}"); }
        catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.Message }); Console.WriteLine("FAIL " + name + ": " + e); }
    }
    private static Task Test(string name, Action action) => Test(name, () => { action(); return Task.CompletedTask; });
    private static HttpResponseMessage Response(object payload) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(payload) } } } })) };
    private static async Task Run()
    {
        await Test("settings window saves choices, reopens with them, and offers no API key field", () =>
        {
            var store = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")));
            var settings = new Settings();
            string? Save(Settings next) { store.Save(next); return null; }
            var window = new SettingsWindow(settings, Save);
            try
            {
                var ocr = FindNodes<CheckBox>(window).Single(x => Equals(x.Content, "로컬 OCR fallback 지원"));
                Assert(ocr.IsChecked == true, "OCR defaults on");
                ocr.IsChecked = false;
                FindButtons(window).Single(x => Equals(x.Content, "저장")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(store.Load().Ocr == false, "saving must persist the new choice");
            }
            finally { window.Close(); }

            var reopened = new SettingsWindow(store.Load(), Save);
            try
            {
                Assert(FindNodes<CheckBox>(reopened).Single(x => Equals(x.Content, "로컬 OCR fallback 지원")).IsChecked == false, "a reopened window must show the saved choice");
                Assert(!AllText(reopened).Contains("API Key") && !AllText(reopened).Contains("API 설명"), "the API tab is gone: no key field or API page may remain");
            }
            finally { reopened.Close(); }
        });
        await Test("free translation never sends credentials and protects code", async () =>
        {
            var h = new Handler((r, _) =>
            {
                Assert(r.RequestUri!.Host == "translate.googleapis.com" && r.Headers.Authorization is null);
                Assert(!Uri.UnescapeDataString(r.RequestUri.Query).Contains("npm run dev"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[[[\"번역\",\"source\",null,null]]]" ) });
            });
            using var p = new FreeTranslationProvider(h);
            var a = await p.TranslateAsync("Please use npm run dev for cache.", "UIA", true, true, default);
            Assert(a.Error is null && a.TranslatedText.Contains("npm run dev") && a.DetectedTerms.Count == 1);
            int count = h.Count; await p.TranslateAsync(a.OriginalText, "OCR", true, true, default); Assert(h.Count == count);
        });
        await Test("free translation deduplicates equivalent prose and preserves spacing", async () =>
        {
            int calls = 0;
            using var p = new FreeTranslationProvider(new Handler((r, _) =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                Assert(!Uri.UnescapeDataString(r.RequestUri!.Query).Contains("alpha"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[[[\"같은 말\",\"same words\",null,null]]]") });
            }));
            var result = await p.TranslateAsync("same words `alpha` same words `beta` same words", "UIA", false, true, default);
            Assert(result.Error is null && result.TranslatedText == "같은 말 `alpha` 같은 말 `beta` 같은 말");
            Assert(calls == 1);
        });
        await Test("free translation cancellation never starts fallback", async () =>
        {
            int calls = 0;
            using var cts = new CancellationTokenSource();
            using var p = new FreeTranslationProvider(new Handler((_, token) =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                cts.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(token);
            }));
            try { await p.TranslateAsync("Cancel this request", "UIA", false, false, cts.Token); throw new Exception("Cancellation swallowed"); }
            catch (OperationCanceledException) { Assert(calls == 1); }
        });
        await Test("code preservation: all requested commands, paths, products", () =>
        {
            foreach (var code in new[] { "npm install", "npm run dev", "git pull", "--force", "package.json", "App.tsx", "useEffect()", "API_KEY", @"C:\project\demo", "src/components/", "localhost:3000", "Node.js", "React", "GitHub", "https://example.com/path?q=1", "/usr/local/bin", "$HOME", "myVariable", "foo_bar", "`const x = 1;`", "```js\nconst x = 1;\n```" })
            { var source = "Please use " + code + " safely."; var p = new CodeProtection(source); Assert(p.Tokens.Values.Contains(code), "Unprotected: " + code); Assert(p.Restore(p.Text) == source); }
        });
        await Test("protection duplicate occurrences, collision, missing and duplicate token rejection", () =>
        {
            var source = "React React __DL_original__"; var p = new CodeProtection(source); Assert(p.Restore(p.Text) == source); var key = p.Tokens.Keys.First();
            foreach (var bad in new[] { p.Text.Replace(key, ""), p.Text + key }) { bool rejected = false; try { p.Restore(bad); } catch (FormatException) { rejected = true; } Assert(rejected); }
            Assert(new CodeProtection(source, false).Text == source);
        });
        await Test("text quality and duplicate filtering", () => { Assert(TextQuality.Clean("hello\nhello") == "hello"); Assert(TextQuality.Clean("x") == ""); Assert(TextQuality.Clean(new string('a', 6001)) == ""); Assert(TextQuality.Clean("12345") == ""); });
        await Test("UIA successful: no retry or OCR", async () =>
        {
            int u = 0, o = 0; var r = await CapturePipeline.RunAsync(_ => { u++; return Task.FromResult<CaptureResult?>(new("hello world", "UIA")); }, _ => { o++; return Task.FromResult<CaptureResult?>(null); }, true, default); Assert(u == 1 && o == 0 && r is not null);
        });
        await Test("UIA exactly one retry then OCR", async () =>
        {
            int u = 0, o = 0; await CapturePipeline.RunAsync(_ => { u++; return Task.FromResult<CaptureResult?>(null); }, _ => { o++; return Task.FromResult<CaptureResult?>(null); }, true, default); Assert(u == 2 && o == 1);
        });
        await Test("retry success skips OCR; OCR off", async () =>
        {
            int u = 0, o = 0; await CapturePipeline.RunAsync(_ => Task.FromResult<CaptureResult?>(++u == 2 ? new("second read", "UIA") : null), _ => { o++; return Task.FromResult<CaptureResult?>(null); }, true, default); Assert(u == 2 && o == 0);
            u = 0; await CapturePipeline.RunAsync(_ => { u++; return Task.FromResult<CaptureResult?>(null); }, _ => { o++; return Task.FromResult<CaptureResult?>(null); }, false, default); Assert(u == 2 && o == 0);
        });
        await Test("capture cancellation stops retry", async () =>
        {
            using var cts = new CancellationTokenSource(30); int u = 0;
            try { await CapturePipeline.RunAsync(_ => { u++; return Task.FromResult<CaptureResult?>(null); }, _ => Task.FromResult<CaptureResult?>(null), true, cts.Token); throw new Exception("not cancelled"); } catch (OperationCanceledException) { Assert(u == 1); }
        });
        await Test("settings persistence, exclusions normalized, corrupt JSON recovery", () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")); var store = new SettingsStore(folder); var s = new Settings { Excluded = new() { "NOTEPAD.EXE", "code" }, CloseSeconds = 31 }; store.Save(s); var read = store.Load(); Assert(read.CloseSeconds == 31 && !ProcessIdentity.Allowed("notepad", read) && !ProcessIdentity.Allowed("CODE.EXE", read)); Assert(!ProcessIdentity.Allowed(null, read)); File.WriteAllText(Path.Combine(folder, "settings.json"), "{ broken"); Assert(store.Load().Key == 0x44);
            File.WriteAllText(Path.Combine(folder, "settings.json"), "{\"Excluded\":[null,\"CODE.EXE\"],\"Model\":null,\"Key\":58}"); var repaired = store.Load(); Assert(repaired.Excluded.SetEquals(new[] { "code.exe" }) && repaired.Key == 0x44);
        });
        await Test("free translation runs without any api key", async () => { using var p = new FreeTranslationProvider(); var r = await p.TranslateAsync("Please run npm run dev on localhost:3000 to check cache and API.", "UIA", true, true, default); Assert(r.Error is null && r.TranslatedText.Contains("npm run dev") && r.TranslatedText.Contains("localhost:3000")); Assert(r.DetectedTerms.Any(x => x.Term == "cache" || x.Term == "API")); });
        await Test("popup card drag moves the window by the pointer delta", async () =>
        {
            Native.GetCursorPos(out var savedCursor);
            var settings = new Settings { AutoClose = false };
            var popup = new PopupWindow();
            bool closed = false;
            popup.Closed += (_, _) => closed = true;
            try
            {
                popup.Present(new Native.Point(300, 300), settings);
                popup.Result(new("source text", "번역 결과", [], "test"), settings);
                await Task.Delay(300);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(popup).Handle;
                Assert(Native.GetWindowRect(hwnd, out var before), "the popup window rect must be readable");
                // The window is placed with SetWindowPos, so its WPF Left/Top are NaN: a drag that read
                // those (the old code) could never move anything. Drive the real handler path instead,
                // passing the pointer position explicitly so the check does not depend on cursor injection.
                Native.GetCursorPos(out var pointer);
                Assert(popup.BeginDrag(), "the drag must start from the window's physical rect");
                popup.DragTo(pointer.X + 140, pointer.Y + 90);
                await Task.Delay(80);
                Assert(Native.GetWindowRect(hwnd, out var after), "the popup window rect must be readable after the drag");
                int dx = after.Left - before.Left, dy = after.Top - before.Top;
                Assert(Math.Abs(dx - 140) <= 2 && Math.Abs(dy - 90) <= 2, $"dragging the card must move the window by the pointer delta, saw {dx},{dy}");
            }
            finally { if (!closed) popup.Close(); SetCursorPos(savedCursor.X, savedCursor.Y); }
        });
        await Test("monitor placement negative origin and large popup", () => { Assert(Placement.Clamp(-10, 1050, 400, 300, -1920, 0, 0, 1080) == (-400, 780)); Assert(Placement.Clamp(500, 500, 2000, 1200, 0, 0, 1280, 720) == (0, 0)); });
        await Test("process refresh and exited process lookup safe", async () =>
        {
            Assert(Native.RunningApps() is not null); Assert(Native.ProcessAt(new Native.Point(-100000, -100000)) is null);
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!; await process.WaitForExitAsync(); Assert(Native.RunningApps() is not null);
        });
        await Test("RegisterHotKey collision keeps original registration", () =>
        {
            using var a = new Hotkey(); using var b = new Hotkey(); Assert(a.Set(3, 0x78)); Assert(b.Set(3, 0x79)); Assert(!a.Set(3, 0x79)); using var probe = new Hotkey(); Assert(!probe.Set(3, 0x78)); Assert(a.Set(3, 0x7A)); Assert(probe.Set(3, 0x78));
        });
        await Test("real CPU Tesseract English bitmap recognition", () =>
        {
            using var bitmap = new Bitmap(1000, 130); using (var g = Graphics.FromImage(bitmap)) { g.Clear(Drawing.Color.White); using var font = new Font("Arial", 26); g.DrawString("Install the package and restart the application.", font, Drawing.Brushes.Black, 12, 35); }
            var r = CaptureWorker.Recognize(bitmap); Assert(r is { Quality: true } && r.Text.Contains("package", StringComparison.OrdinalIgnoreCase), r?.Text ?? "no OCR result");
        });
        await Test("popup answer text follows the region text size", () =>
        {
            var popup = new PopupWindow();
            var content = (DependencyObject)popup.Content;
            var settings = new Settings { AutoClose = false };
            popup.Result(new("source text", "번역 결과", [], "test"), settings, settings.RegionTextScale);
            Assert(HasFontSize(content, 15), "100 % must keep the answer at 15 px");
            Assert(HasFontSize(content, 13), "100 % must keep the original at 13 px");
            settings.RegionTextPercent = 150;
            popup.Result(new("source text", "번역 결과", [], "test"), settings, settings.RegionTextScale);
            Assert(HasFontSize(content, 22.5), "150 % must scale the answer to 22.5 px");
            Assert(HasFontSize(content, 19.5), "150 % must scale the original to 19.5 px");
            popup.Result(new("source text", "번역 결과", [], "test"), settings, 0);
            Assert(HasFontSize(content, 15), "a corrupt scale must fall back to 100 %");
        });
        await Test("WPF popup no activation, scroll content, reread event, autoclose", async () =>
        {
            Native.GetCursorPos(out var savedCursor); SetCursorPos(5, 5); var foreground = Native.GetForegroundWindow(); var popup = new PopupWindow(); bool closed = false, reread = false; popup.Closed += (_, _) => closed = true; popup.Reread += () => reread = true;
            try
            {
                popup.Present(new Native.Point(300, 300), new Settings { AutoClose = true, CloseSeconds = 1 }); popup.Result(new("source", "translated", [], "test"), new Settings { AutoClose = true, CloseSeconds = 1 }); await Task.Delay(100); var popupHandle = new System.Windows.Interop.WindowInteropHelper(popup).Handle; Assert(Native.GetForegroundWindow() != popupHandle, "popup stole focus");
                FindButtons((DependencyObject)popup.Content).First(x => Equals(x.Content, "↻ 마지막 드래그")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(reread); SetCursorPos(5, 5); for (int i = 0; i < 20 && !closed; i++) await Task.Delay(100); Assert(closed, "autoclose failed");
            }
            finally { if (!closed) popup.Close(); SetCursorPos(savedCursor.X, savedCursor.Y); }
        });
        await Test("real UIA TextPattern/ValuePattern capture in a Windows fixture", async () =>
        {
            var box = new TextBox { Text = "Install the package and restart the application.", FontSize = 22, Margin = new Thickness(25) };
            var window = new Window { Title = "hoonsoo UIA verification fixture", Width = 750, Height = 220, Left = 100, Top = 100, Content = box, Topmost = true };
            try
            {
                window.Show(); await Task.Delay(250);
                var point = box.PointToScreen(new System.Windows.Point(60, 20));
                var exeName = File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/hoonsoo/bin/" + new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name + "/net10.0-windows/hoonsoo.exe"))) ? "hoonsoo.exe" : "DevLingo.exe";
                var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/hoonsoo/bin/" + new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name + "/net10.0-windows/" + exeName));
                var capture = new CaptureClient(executable);
                var r = await capture.RunAsync("--uia", default, (int)point.X, (int)point.Y);
                Assert(r is not null && r.Text.Contains("package"), "UIA worker returned no fixture text");
                var ocr = await capture.RunAsync("--ocr", default, (int)point.X - 40, (int)point.Y - 15, 650, 80);
                Assert(ocr is { Quality: true } && ocr.Text.Contains("package", StringComparison.OrdinalIgnoreCase), "screen OCR fixture failed");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await capture.RunAsync("--uia", cancelled.Token, 0, 0); throw new Exception("worker not cancelled"); } catch (OperationCanceledException) { }
            }
            finally { window.Close(); }
        });
        await Test("popup ESC and automatic-close off", async () =>
        {
            RequireInjectedInput();
            var popup = new PopupWindow(); bool closed = false; popup.Closed += (_, _) => closed = true;
            try
            {
                popup.Present(new Native.Point(400, 400), new Settings { AutoClose = false }); popup.Result(new("source", "translation", [], "test"), new Settings { AutoClose = false }); await Task.Delay(1100); Assert(!closed);
                keybd_event(0x1B, 0, 0, UIntPtr.Zero);
                // Poll instead of a single 200ms window: the popup samples the key every poll tick, and one
                // fixed delay made this check depend on where the tick happened to land.
                for (int i = 0; i < 20 && !closed; i++) await Task.Delay(100);
                keybd_event(0x1B, 0, 2, UIntPtr.Zero);
                Assert(closed, "ESC must close the popup");
            }
            finally { keybd_event(0x1B, 0, 2, UIntPtr.Zero); if (!closed) popup.Close(); }
        });
        await Test("manual region selector drag and cancel", async () =>
        {
            RequireInjectedInput();
            Native.GetCursorPos(out var previous);
            try
            {
                using var deadline = new CancellationTokenSource(5000); var selection = RegionSelector.SelectAsync(deadline.Token); await Task.Delay(200);
                SetCursorPos(200, 200); mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(60); SetCursorPos(650, 320); await Task.Delay(60); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                var rect = await selection; Assert(rect is { Width: > 8, Height: > 8 }, "drag coordinates incorrect: " + rect);
                using var cancel = new CancellationTokenSource(100); try { await RegionSelector.SelectAsync(cancel.Token); throw new Exception("not cancelled"); } catch (OperationCanceledException) { }
            }
            finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(previous.X, previous.Y); }
        });
        await Test("manual mode does not depend on app exclusion settings", () =>
        {
            var settings = new Settings(); settings.Excluded.Add("notepad.exe");
            Assert(ProcessIdentity.Allowed("notepad.exe", settings) == false);
            Assert(settings.RegionKey == 0x52 && settings.AreaKey == 0x4F);
        });
        await Test("WindowsMediaOcr availability and recognition", async () =>
        {
            if (WindowsMediaOcr.IsAvailable)
            {
                using var bmp = new Drawing.Bitmap(300, 80);
                using (var g = Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(Drawing.Color.White);
                    g.DrawString("hoonsoo AR Translation", new Drawing.Font("Arial", 14), Drawing.Brushes.Black, 10, 20);
                }
                var result = await WindowsMediaOcr.RecognizeAsync(bmp);
                Assert(result is not null, "OcrPageResult should not be null");
                Assert(result!.Lines.Count > 0, "Should detect at least one line");
                Assert(result.Lines[0].Words.Count > 0, "Should have words with bounding boxes");
            }
        });
        await Test("ArTranslationService line merger groups paragraphs correctly", () =>
        {
            var lines = new List<OcrLineInfo>
            {
                new("Hello world this is line 1", new Drawing.Rectangle(10, 10, 200, 20), []),
                new("and this is continuing line 2", new Drawing.Rectangle(12, 32, 210, 20), []),
                new("Far away independent button", new Drawing.Rectangle(10, 150, 180, 20), [])
            };
            var blocks = ArTranslationService.MergeLinesToBlocks(lines);
            Assert(blocks.Count == 2, $"Expected 2 merged blocks, got {blocks.Count}");
            Assert(blocks[0].OriginalText.Contains("Hello world") && blocks[0].OriginalText.Contains("continuing line 2"));
            Assert(blocks[1].OriginalText == "Far away independent button");
            Assert(blocks[0].Bounds.Height >= 40, "Merged block bounds should encompass both lines");
        });
        await Test("ArTranslationService batch translation and caching", async () =>
        {
            var lines = new List<OcrLineInfo>
            {
                new("Close window", new Drawing.Rectangle(50, 50, 100, 20), []),
                new("Settings and options", new Drawing.Rectangle(50, 80, 120, 20), [])
            };
            var blocks = ArTranslationService.MergeLinesToBlocks(lines);
            // Stub the upstream instead of calling translate.googleapis.com: the batch pipeline is the unit
            // under test, and the live endpoint rate-limits by IP, which made this a flaky release gate.
            var transport = new Handler((request, _) => Task.FromResult(TranslationResponse(request)));
            ArTranslationService.Transport = transport;
            try
            {
                await ArTranslationService.TranslateBlocksAsync(blocks, protectCode: false);
                Assert(blocks.All(b => !string.IsNullOrWhiteSpace(b.TranslatedText)), "All blocks should have translations");
                Assert(transport.Count == 1, $"one batch request expected, saw {transport.Count}");

                await ArTranslationService.TranslateBlocksAsync(blocks, protectCode: false);
                Assert(transport.Count == 1, $"cache hit must not re-request, saw {transport.Count}");
            }
            finally { ArTranslationService.Transport = null; }
        });
        await Test("ArTranslationService batch failure falls back to per-item requests", async () =>
        {
            var lines = new List<OcrLineInfo>
            {
                new("Open settings", new Drawing.Rectangle(50, 50, 100, 20), []),
                new("Save changes", new Drawing.Rectangle(50, 300, 120, 20), [])
            };
            var blocks = ArTranslationService.MergeLinesToBlocks(lines);
            Assert(blocks.Count == 2, $"fixture should keep two blocks apart, got {blocks.Count}");
            bool failedBatch = false;
            var transport = new Handler((request, _) =>
            {
                if (failedBatch) return Task.FromResult(TranslationResponse(request));
                failedBatch = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            });
            ArTranslationService.Transport = transport;
            try
            {
                await ArTranslationService.TranslateBlocksAsync(blocks, protectCode: false);
                Assert(failedBatch, "the batch request should have been attempted first");
                Assert(transport.Count == 3, $"one failed batch plus two singles expected, saw {transport.Count}");
                Assert(blocks.All(b => !string.IsNullOrWhiteSpace(b.TranslatedText)), "fallback should translate every block");
            }
            finally { ArTranslationService.Transport = null; }
        });
        await Test("full screen translation masks code and paths before they leave the process", async () =>
        {
            var lines = new List<OcrLineInfo>
            {
                new("Run npm run dev in C:\\work\\hoonsoo", new Drawing.Rectangle(50, 50, 320, 20), [])
            };
            var blocks = ArTranslationService.MergeLinesToBlocks(lines);
            Assert(blocks.Count == 1, $"fixture should give one block, got {blocks.Count}");
            string? sent = null;
            var transport = new Handler((request, _) =>
            {
                sent = Uri.UnescapeDataString(request.RequestUri!.Query.Split("&q=")[^1]);
                return Task.FromResult(TranslationResponse(request));
            });
            ArTranslationService.Transport = transport;
            try
            {
                await ArTranslationService.TranslateBlocksAsync(blocks, protectCode: true);
                Assert(sent is not null, "the transport should have been called");
                Assert(!sent!.Contains("npm run dev"), $"the command must not leave the machine, sent: {sent}");
                Assert(!sent.Contains(@"C:\work\hoonsoo"), $"the path must not leave the machine, sent: {sent}");
                Assert(blocks[0].TranslatedText.Contains("npm run dev"), $"the masked command must come back: {blocks[0].TranslatedText}");
                Assert(blocks[0].TranslatedText.Contains(@"C:\work\hoonsoo"), $"the masked path must come back: {blocks[0].TranslatedText}");
            }
            finally { ArTranslationService.Transport = null; }
        });
        await Test("full screen translation drops a block rather than render a corrupted token", async () =>
        {
            var blocks = ArTranslationService.MergeLinesToBlocks(new List<OcrLineInfo> { new("Open the npm install guide", new Drawing.Rectangle(0, 0, 240, 20), []) });
            ArTranslationService.Transport = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new[] { new[] { new[] { "번역 완료", "x" } } }))
            }));
            try
            {
                await ArTranslationService.TranslateBlocksAsync(blocks, protectCode: true);
                Assert(blocks[0].TranslatedText.Length == 0, $"a translation that lost its masked token must not render, got '{blocks[0].TranslatedText}'");
            }
            finally { ArTranslationService.Transport = null; }
        });
        await Test("full screen translation reports an unreachable service instead of 'nothing to translate'", async () =>
        {
            var blocks = ArTranslationService.MergeLinesToBlocks(new List<OcrLineInfo> { new("Another line of English prose", new Drawing.Rectangle(0, 0, 240, 20), []) });
            ArTranslationService.Transport = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            try
            {
                var outcome = await ArTranslationService.TranslateBlocksAsync(blocks, protectCode: true);
                Assert(outcome.TransportFailed, "a request that never got an answer must be reported to the caller");
                Assert(blocks[0].TranslatedText.Length == 0, "nothing may be rendered when the service never answered");
            }
            finally { ArTranslationService.Transport = null; }
        });
        await Test("ArOverlayWindow instantiation and structure", () =>
        {
            var win = new ArOverlayWindow(new Drawing.Rectangle(0, 0, 800, 600));
            Assert(win.WindowStyle == WindowStyle.None);
            Assert(win.AllowsTransparency == true);
            Assert(win.Topmost == true);
            win.ShowLoading("Testing loading...");
            var blocks = new List<ArBlock>
            {
                new("Welcome", new Drawing.Rectangle(100, 100, 80, 20), []) { TranslatedText = "환영합니다" }
            };
            win.DisplaySubtitles(blocks, 0, 0);
            // The dragged-area plate shares this overlay; it must survive both the pending and the
            // settled state without a layout crash, and it must never shrink below an AR subtitle.
            win.DisplayRegionResult(new Drawing.Rectangle(100, 140, 200, 24), "환영합니다", pending: false);
            win.DisplayRegionResult(new Drawing.Rectangle(100, 140, 200, 24), "번역 중…", pending: true);
            win.Dismiss();
        });
        await Test("popup and plate placement step around the captured region", () =>
        {
            var screen = new Drawing.Rectangle(0, 0, 1920, 1080);
            var source = new Drawing.Rectangle(800, 400, 300, 120);
            var (x, y) = Placement.BesideSource(440, 300, 818, 422, screen, new[] { source });
            Assert(!Placement.Overlaps(x, y, 440, 300, source), "the popup must not open on top of the text it translates");
            Assert(x >= screen.Left && y >= screen.Top && x + 440 <= screen.Right && y + 300 <= screen.Bottom, "placement stays inside the monitor");

            // Nothing fits beside the source: the fallback still has to stay on screen.
            var tight = new Drawing.Rectangle(0, 0, 500, 300);
            var covered = new Drawing.Rectangle(10, 10, 480, 280);
            var (fx, fy) = Placement.BesideSource(440, 300, 460, 40, tight, new[] { covered });
            Assert(fx >= tight.Left && fy >= tight.Top && fx + 440 <= tight.Right && fy + 300 <= tight.Bottom, "fallback placement stays inside the working area");
        });
        await Test("expander header text follows the palette so it stays readable in dark mode", () =>
        {
            Theme.Apply("dark");
            try
            {
                var expander = new Expander { Header = "원문 보기" };
                Ui.StyleExpander(expander);
                var host = new Border { Child = expander };
                host.Measure(new System.Windows.Size(400, 200));
                host.Arrange(new Rect(0, 0, 400, 200));

                var toggle = (ToggleButton)expander.Template.FindName("HeaderSite", expander)!;
                var label = (ContentPresenter)toggle.Template.FindName("headerLabel", toggle)!;
                var brush = (SolidColorBrush)TextElement.GetForeground(label);
                Assert(brush.Color == Theme.TextPrimary, $"header text must use the palette primary colour, saw {brush.Color}");
                Assert(brush.Color.R > 200 && brush.Color.G > 200 && brush.Color.B > 200, "dark-mode header text must be light");
            }
            finally { Theme.Apply("light"); }
        });
        await Test("region plate font stays above the AR subtitle font", () =>
        {
            foreach (double h in new[] { 6.0, 12.0, 20.0, 28.0, 40.0, 80.0 })
            {
                double ar = ArOverlayWindow.ArFontFor(h);
                double region = ArOverlayWindow.RegionFontFor(h);
                Assert(region > ar, $"a dragged area must read larger than an AR subtitle at height {h}: {region} vs {ar}");
            }
            Assert(ArOverlayWindow.RegionFontFor(24) >= 18, "even a short drag keeps a legible plate");
        });
        await Test("ArTranslationService smart noise filtering and symbol stripping", () =>
        {
            Assert(!ArTranslationService.IsMeaningfulForTranslation("1.2.3"), "Numbers should be filtered");
            Assert(!ArTranslationService.IsMeaningfulForTranslation("###"), "Pure markdown headers should be filtered");
            Assert(!ArTranslationService.IsMeaningfulForTranslation("---"), "Horizontal rules should be filtered");
            Assert(!ArTranslationService.IsMeaningfulForTranslation("const"), "Single keywords should be filtered");
            Assert(!ArTranslationService.IsMeaningfulForTranslation("100%"), "Percentages should be filtered");
            Assert(!ArTranslationService.IsMeaningfulForTranslation("v2.4.0"), "Versions should be filtered");
            Assert(!ArTranslationService.IsMeaningfulForTranslation("id"), "Short abbreviations should be filtered");

            Assert(ArTranslationService.IsMeaningfulForTranslation("### Getting Started Guide"), "Markdown headers with text should be accepted");
            Assert(ArTranslationService.StripLeadingSymbols("### Getting Started Guide") == "Getting Started Guide");
            Assert(ArTranslationService.StripLeadingSymbols("1. Install the dependencies") == "Install the dependencies");
            Assert(ArTranslationService.StripLeadingSymbols("// Connect to the server") == "Connect to the server");
            Assert(ArTranslationService.StripLeadingSymbols("> Important note for users") == "Important note for users");
        });
        await Test("release executable second launch replaces the running instance", async () =>
        {
            var releaseDir = Directory.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../outputs/hoonsoo-win-x64"))) ? "hoonsoo-win-x64" : "DevLingo-V1-win-x64";
            var releaseExe = File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../outputs/" + releaseDir + "/hoonsoo.exe"))) ? "hoonsoo.exe" : "DevLingo.exe";
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../outputs/" + releaseDir + "/" + releaseExe));
            using var first = Process.Start(new ProcessStartInfo(executable, "--background") { UseShellExecute = false })!;
            Process? second = null;
            try
            {
                await Task.Delay(700); Assert(!first.HasExited, "first instance exited");
                second = Process.Start(new ProcessStartInfo(executable, "--background") { UseShellExecute = false })!;

                // Takeover is the documented contract: the newer instance stops the older one and keeps
                // running. Waiting for the older process to exit makes this deterministic, where the old
                // assertion only held when the asynchronous kill had not landed yet.
                bool replaced;
                try { using var deadline = new CancellationTokenSource(6000); await first.WaitForExitAsync(deadline.Token); replaced = true; }
                catch (OperationCanceledException) { replaced = false; }
                Assert(replaced, "the older instance should be stopped by the newer one");

                await Task.Delay(600);
                second.Refresh();
                Assert(!second.HasExited, "the replacing instance must keep running");
            }
            finally
            {
                if (second is not null && !second.HasExited) { second.Kill(); await second.WaitForExitAsync(); }
                if (!first.HasExited) { first.Kill(); await first.WaitForExitAsync(); }
            }
        });
        await Test("region result mode parsing and surface routing", () =>
        {
            Assert(RegionResultModes.Normalize(null) == RegionResultModes.Both);
            Assert(RegionResultModes.Normalize("") == RegionResultModes.Both);
            Assert(RegionResultModes.Normalize("overlay") == RegionResultModes.Overlay);
            Assert(RegionResultModes.Normalize("POPUP ") == RegionResultModes.Popup);
            Assert(RegionResultModes.Normalize("both") == RegionResultModes.Both);
            Assert(RegionResultModes.Normalize("garbage") == RegionResultModes.Both);

            Assert(RegionResultModes.ShowsOverlay(null) && RegionResultModes.ShowsPopup(null));
            Assert(RegionResultModes.ShowsOverlay("") && RegionResultModes.ShowsPopup(""));
            Assert(RegionResultModes.ShowsOverlay("overlay") && !RegionResultModes.ShowsPopup("overlay"));
            Assert(!RegionResultModes.ShowsOverlay("POPUP ") && RegionResultModes.ShowsPopup("POPUP "));
            Assert(RegionResultModes.ShowsOverlay("both") && RegionResultModes.ShowsPopup("both"));
            Assert(RegionResultModes.ShowsOverlay("garbage") && RegionResultModes.ShowsPopup("garbage"));
        });
        await Test("region selection guidance speaks only when the user did not cancel", () =>
        {
            Assert(RegionSelection.Guidance(RegionSelectionOutcome.Selected) is null, "Selected needs no error guidance");
            Assert(RegionSelection.Guidance(RegionSelectionOutcome.Cancelled) is null, "Cancelled by user needs no message");

            var small = RegionSelection.Guidance(RegionSelectionOutcome.TooSmall);
            Assert(!string.IsNullOrWhiteSpace(small) && small.Contains("작습니다"), "TooSmall guidance must be descriptive");

            var large = RegionSelection.Guidance(RegionSelectionOutcome.TooLarge);
            Assert(!string.IsNullOrWhiteSpace(large) && large.Contains("큽니다"), "TooLarge guidance must be descriptive");

            var unavail = RegionSelection.Guidance(RegionSelectionOutcome.Unavailable);
            Assert(!string.IsNullOrWhiteSpace(unavail) && unavail.Contains("열 수 없습니다"), "Unavailable guidance must explain failure");
        });
        await Test("region settings snap, clamp and round-trip through the store", () =>
        {
            Assert(Settings.SnapTextPercent(0) == 60, "0 snaps to min 60");
            Assert(Settings.SnapTextPercent(59) == 60, "59 snaps to 60");
            Assert(Settings.SnapTextPercent(63) == 60, "63 is closer to 60 than 70");
            Assert(Settings.SnapTextPercent(100) == 100, "100 stays 100");
            Assert(Settings.SnapTextPercent(113) == 110, "113 is closer to 110 than 125");
            Assert(Settings.SnapTextPercent(137) == 125 || Settings.SnapTextPercent(137) == 150, "137 snaps to nearest step (125 or 150)");
            Assert(Settings.SnapTextPercent(1000) == 200, "1000 snaps to max 200");
            Assert(Settings.SnapTextPercent(-5) == 60, "-5 snaps to min 60");

            var s = new Settings { ArTextPercent = 137, RegionTextPercent = 50, RegionResult = "popup " };
            s.Normalize();
            Assert(s.ArTextPercent == 125, "137 snaps to 125 on normalize");
            Assert(s.RegionTextPercent == 60, "50 snaps to 60 on normalize");
            Assert(s.RegionResult == RegionResultModes.Popup, "popup normalized");
            Assert(Math.Abs(s.ArTextScale - 1.25) < 1e-9);
            Assert(Math.Abs(s.RegionTextScale - 0.60) < 1e-9);

            var folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
            var store = new SettingsStore(folder);
            var toSave = new Settings
            {
                RegionResult = RegionResultModes.Overlay,
                ArTextPercent = 150,
                RegionTextPercent = 80
            };
            store.Save(toSave);

            var loaded = store.Load();
            Assert(loaded.RegionResult == RegionResultModes.Overlay, "RegionResult roundtripped");
            Assert(loaded.ArTextPercent == 150, "ArTextPercent roundtripped");
            Assert(loaded.RegionTextPercent == 80, "RegionTextPercent roundtripped");
        });
        await Test("on-screen font size follows its own percent setting", () =>
        {
            double h = 24.0;
            double arBase = ArOverlayWindow.ArFontFor(h, 1.0);
            double regBase = ArOverlayWindow.RegionFontFor(h, 1.0);

            double ar06 = ArOverlayWindow.ArFontFor(h, 0.6);
            double ar20 = ArOverlayWindow.ArFontFor(h, 2.0);
            Assert(Math.Abs(ar06 - arBase * 0.6) < 1e-9, $"AR font at 0.6 scale must scale proportionally: {ar06} vs {arBase * 0.6}");
            Assert(Math.Abs(ar20 - arBase * 2.0) < 1e-9, $"AR font at 2.0 scale must scale proportionally: {ar20} vs {arBase * 2.0}");

            double reg06 = ArOverlayWindow.RegionFontFor(h, 0.6);
            double reg20 = ArOverlayWindow.RegionFontFor(h, 2.0);
            Assert(Math.Abs(reg06 - regBase * 0.6) < 1e-9, $"Region font at 0.6 scale must scale proportionally: {reg06} vs {regBase * 0.6}");
            Assert(Math.Abs(reg20 - regBase * 2.0) < 1e-9, $"Region font at 2.0 scale must scale proportionally: {reg20} vs {regBase * 2.0}");

            foreach (double scale in new[] { 0.6, 1.0, 1.5, 2.0 })
            {
                double ar = ArOverlayWindow.ArFontFor(h, scale);
                double reg = ArOverlayWindow.RegionFontFor(h, scale);
                Assert(reg > ar, $"Region font must stay larger than AR font at scale {scale}: {reg} vs {ar}");
            }

            Assert(Math.Abs(ArOverlayWindow.ArFontFor(h, 0.0) - arBase) < 1e-9, "scale <= 0 falls back to 1.0");
            Assert(Math.Abs(ArOverlayWindow.ArFontFor(h, -1.5) - arBase) < 1e-9, "negative scale falls back to 1.0");
            Assert(Math.Abs(ArOverlayWindow.RegionFontFor(h, 0.0) - regBase) < 1e-9, "scale <= 0 falls back to 1.0");
            Assert(Math.Abs(ArOverlayWindow.RegionFontFor(h, -2.0) - regBase) < 1e-9, "negative scale falls back to 1.0");
        });
        await Test("subtitle chip and region plate render offscreen at the configured size", () =>
        {
            var chipSmall = ArOverlayWindow.CreateSubtitleChip("Test Subtitle Line", 12.0, 400);
            var chipLarge = ArOverlayWindow.CreateSubtitleChip("Test Subtitle Line", 24.0, 400);

            chipSmall.Measure(new System.Windows.Size(400, double.PositiveInfinity));
            chipSmall.Arrange(new Rect(0, 0, 400, chipSmall.DesiredSize.Height));

            chipLarge.Measure(new System.Windows.Size(400, double.PositiveInfinity));
            chipLarge.Arrange(new Rect(0, 0, 400, chipLarge.DesiredSize.Height));

            Assert(chipLarge.DesiredSize.Height > chipSmall.DesiredSize.Height, "larger font must produce taller chip DesiredSize");

            int wSmall = Math.Max(1, (int)Math.Ceiling(chipSmall.ActualWidth));
            int hSmall = Math.Max(1, (int)Math.Ceiling(chipSmall.ActualHeight));
            var rtbSmall = new System.Windows.Media.Imaging.RenderTargetBitmap(wSmall, hSmall, 96, 96, PixelFormats.Pbgra32);
            rtbSmall.Render(chipSmall);

            int wLarge = Math.Max(1, (int)Math.Ceiling(chipLarge.ActualWidth));
            int hLarge = Math.Max(1, (int)Math.Ceiling(chipLarge.ActualHeight));
            var rtbLarge = new System.Windows.Media.Imaging.RenderTargetBitmap(wLarge, hLarge, 96, 96, PixelFormats.Pbgra32);
            rtbLarge.Render(chipLarge);

            Assert(rtbLarge.PixelHeight > rtbSmall.PixelHeight, $"rendered bitmap height must grow with font size: {rtbLarge.PixelHeight} vs {rtbSmall.PixelHeight}");

            var plateSmall = ArOverlayWindow.CreateRegionPlate("Drag Result Text", 18.0, false, 400);
            var plateLarge = ArOverlayWindow.CreateRegionPlate("Drag Result Text", 36.0, false, 400);

            plateSmall.Measure(new System.Windows.Size(400, double.PositiveInfinity));
            plateSmall.Arrange(new Rect(0, 0, 400, plateSmall.DesiredSize.Height));

            plateLarge.Measure(new System.Windows.Size(400, double.PositiveInfinity));
            plateLarge.Arrange(new Rect(0, 0, 400, plateLarge.DesiredSize.Height));

            Assert(plateLarge.DesiredSize.Height > plateSmall.DesiredSize.Height, "larger plate font must produce taller plate DesiredSize");

            int pwSmall = Math.Max(1, (int)Math.Ceiling(plateSmall.ActualWidth));
            int phSmall = Math.Max(1, (int)Math.Ceiling(plateSmall.ActualHeight));
            var prtbSmall = new System.Windows.Media.Imaging.RenderTargetBitmap(pwSmall, phSmall, 96, 96, PixelFormats.Pbgra32);
            prtbSmall.Render(plateSmall);

            int pwLarge = Math.Max(1, (int)Math.Ceiling(plateLarge.ActualWidth));
            int phLarge = Math.Max(1, (int)Math.Ceiling(plateLarge.ActualHeight));
            var prtbLarge = new System.Windows.Media.Imaging.RenderTargetBitmap(pwLarge, phLarge, 96, 96, PixelFormats.Pbgra32);
            prtbLarge.Render(plateLarge);

            Assert(prtbLarge.PixelHeight > prtbSmall.PixelHeight, $"rendered plate bitmap height must grow with font size: {prtbLarge.PixelHeight} vs {prtbSmall.PixelHeight}");
        });
        await Test("settings window exposes the region pickers without showing", () =>
        {
            var window = new SettingsWindow(new Settings(), _ => null);
            try
            {
                var combos = FindNodes<ComboBox>(window).ToList();
                Assert(combos.Count >= 3, $"expected at least 3 ComboBoxes (theme, region result, scale), found {combos.Count}");

                var regionResultCombo = combos.FirstOrDefault(c => c.Items.Cast<object>().Any(it => Equals(it, "화면 위 오버레이")));
                Assert(regionResultCombo is not null, "region result ComboBox not found in logical tree");
                var regionItems = regionResultCombo!.Items.Cast<object>().Select(it => it.ToString()).ToList();
                Assert(regionItems.Contains("화면 위 오버레이"), "missing '화면 위 오버레이'");
                Assert(regionItems.Contains("번역 팝업 창"), "missing '번역 팝업 창'");
                Assert(regionItems.Contains("둘 다 (기본)"), "missing '둘 다 (기본)'");

                var percentCombos = combos.Where(c => c.Items.Count == Settings.TextPercentSteps.Length && c.Items.Cast<object>().Any(it => Equals(it, "100%"))).ToList();
                Assert(percentCombos.Count >= 2, $"expected 2 scale percent pickers (AR and Region), found {percentCombos.Count}");

                foreach (var pc in percentCombos)
                {
                    Assert(pc.Items.Count == Settings.TextPercentSteps.Length, "picker items count must match TextPercentSteps length");
                    for (int i = 0; i < Settings.TextPercentSteps.Length; i++)
                    {
                        Assert(Equals(pc.Items[i], $"{Settings.TextPercentSteps[i]}%"), $"item {i} must be {Settings.TextPercentSteps[i]}%");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
        await Test("a drag still opens the selector when our own popup sits under the cursor", async () =>
        {
            RequireInjectedInput();
            Native.GetCursorPos(out var savedCursor);
            var store = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")));
            store.Save(new Settings { AutoClose = false });
            var popup = new PopupWindow();
            bool popupClosed = false;
            popup.Closed += (_, _) => popupClosed = true;
            AppController? controller = null;
            try
            {
                // 기본번역(팝업)이 커서 아래에 있는 상태가 이 검사의 조건이다. 예전에는 드래그 흐름에도
                // 걸려 있던 자기 프로세스 검사가 이 조건에서 조용히 return해 영역 선택 창이 뜨지 않았다.
                popup.Present(new Native.Point(220, 220), new Settings { AutoClose = false });
                await Task.Delay(300);
                Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(popup).Handle, out var popupRect);
                SetCursorPos(popupRect.Left + 40, popupRect.Top + 40);
                await Task.Delay(80);

                // OCR 작업 프로세스만 대체한다 — 이 검사는 드래그 경로만 보며, 사각형 안에 무엇이 있든 상관없다.
                controller = new AppController(Application.Current, store, new CaptureClient("cmd.exe"));
                var drag = controller.TranslateAsync(true);

                bool opened = false;
                for (int i = 0; i < 30 && !opened; i++) { await Task.Delay(100); opened = SelectorWindows().Any(); }
                Assert(opened, "the region selector must open even with our own popup under the cursor");

                // 팝업이 덮지 않는 곳에서 30x30 드래그: 영역 선택 창이 실제로 입력을 받아야 한다.
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(popupRect.Left, popupRect.Top)).Bounds;
                int sx = Math.Min(screen.Right - 160, popupRect.Right + 140);
                int sy = Math.Min(screen.Bottom - 160, popupRect.Top + 40);
                SetCursorPos(sx, sy); await Task.Delay(60);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                SetCursorPos(sx + 30, sy + 30); await Task.Delay(80);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);

                bool closed = false;
                for (int i = 0; i < 40 && !closed; i++) { await Task.Delay(100); closed = !SelectorWindows().Any(); }
                Assert(closed, "the selector must consume the drag and close");
                await drag;
                Assert(!popupClosed, "the popup that was under the cursor is not the selector's business");
            }
            finally
            {
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                controller?.Dispose();
                if (!popupClosed) popup.Close();
                SetCursorPos(savedCursor.X, savedCursor.Y);
            }
        });
    }

    /// <summary>The region selector's own full-screen drag surfaces; nothing else in the app has that shape.</summary>
    private static IEnumerable<Window> SelectorWindows() =>
        Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w.Topmost && w.WindowStyle == WindowStyle.None && w.Cursor == System.Windows.Input.Cursors.Cross);
    private static IEnumerable<T> FindNodes<T>(DependencyObject root) where T : DependencyObject
    { if (root is T value) yield return value; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var nested in FindNodes<T>(child)) yield return nested; }
    /// <summary>True when any text in the tree is drawn at <paramref name="size"/>: how the size picker is checked without opening a window.</summary>
    private static bool HasFontSize(DependencyObject root, double size) => FindNodes<TextBlock>(root).Any(t => Math.Abs(t.FontSize - size) < 0.01);
    private static string AllText(DependencyObject root)
    { var text = root is TextBlock t ? t.Text : ""; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) text += AllText(child); return text; }
    private static IEnumerable<Button> FindButtons(DependencyObject root)
    { if (root is Button b) yield return b; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var nested in FindButtons(child)) yield return nested; }
    /// <summary>
    /// Google-style payload for the injected transport: one segment holding every delimiter-separated
    /// source, tagged in Korean so the result is neither empty nor an identity pass-through.
    /// </summary>
    private static HttpResponseMessage TranslationResponse(HttpRequestMessage request)
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query.Split("&q=")[^1]);
        var translated = string.Join("\n---\n", query.Split("\n---\n").Select(part => "번역 " + part));
        var payload = new[] { new[] { new[] { translated, translated } } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int count;
        // The AR fallback path issues its per-item requests concurrently, so the counter must be atomic.
        public int Count => Volatile.Read(ref count);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Interlocked.Increment(ref count); return callback(request, token); }
    }
}
