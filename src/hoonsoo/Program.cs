using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace Hoonsoo;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--uia" or "--ocr") return CaptureWorker.Run(args);
        using var instance = new Mutex(true, @"Local\hoonsoo.V1", out bool first);
        if (!first)
        {
            // A double-clicked newer exe used to exit silently while an older tray
            // instance held the mutex. Take over instead: stop earlier instances
            // (never our own younger workers) and claim the mutex they leave behind.
            // Windows reports that handover as an abandoned mutex, and waiting on it
            // *does* succeed — treating it as failure is what made the replacing
            // instance exit and leave the app dead after a double-click.
            ReplaceOlderInstances();
            try { first = instance.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { first = true; }
            catch { first = false; }
            if (!first) return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var controller = new AppController(app);
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; controller.Recover(); };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        app.Startup += (_, _) => controller.Start(args.Contains("--background"));
        app.Run(); instance.ReleaseMutex(); return 0;
    }
    private static void ReplaceOlderInstances()
    {
        var self = Process.GetCurrentProcess();
        foreach (var name in new[] { "hoonsoo", "DevLingo" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == self.Id) continue;
                try
                {
                    // Workers (--uia/--ocr) start after us; only stop earlier mains.
                    if (p.StartTime > self.StartTime) continue;
                    p.Kill();
                }
                catch { }
            }
        }
    }
}
public sealed class AppController : IDisposable
{
    // The rename to hoonsoo.exe left the self-check comparing against "devlingo.exe", so the app
    // could OCR its own surface. Resolve ourselves from the entry assembly + process instead.
    private static readonly HashSet<string> SelfIdentities = BuildSelfIdentities();

    private static HashSet<string> BuildSelfIdentities()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "devlingo.exe" };
        try { names.Add(ProcessIdentity.Normalize(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "hoonsoo")); } catch { }
        try { names.Add(ProcessIdentity.Normalize(System.Diagnostics.Process.GetCurrentProcess().ProcessName)); } catch { }
        return names;
    }

    private readonly Application app;
    private readonly SettingsStore store;
    private readonly Hotkey hotkey = new();
    private readonly Hotkey regionHotkey = new();
    private readonly Hotkey areaHotkey = new();
    private readonly Hotkey arHotkey = new();
    private readonly CaptureClient capture;
    private readonly Forms.NotifyIcon tray;
    private Settings settings;
    private FreeTranslationProvider provider;
    private CancellationTokenSource? request;
    private PopupWindow? popup;
    private ArOverlayWindow? arOverlay;
    private SettingsWindow? settingsWindow;
    private bool enabled = true;
    private long generation;
    private bool disposed;
    private bool followingSystemTheme;
    private Rectangle? lastRegion;
    // What the popup must not cover: the captured text and the dragged-area plate on screen.
    private Rectangle? sourceRegion;
    private Rectangle? plateRect;
    public AppController(Application app, SettingsStore? settingsStore = null, CaptureClient? captureClient = null)
    {
        store = settingsStore ?? new SettingsStore(); capture = captureClient ?? new CaptureClient();
        this.app = app; settings = store.Load(); provider = NewProvider();
        // Must precede the NotifyIcon: the WinForms colour-mode call only affects controls created after
        // it, and the tray menu is the one surface the shared palette cannot reach directly.
        ApplyTheme(settings.Theme);
        Icon trayIcon;
        try { trayIcon = new Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico")); } catch { trayIcon = SystemIcons.Information; }
        tray = new Forms.NotifyIcon { Text = "훈수 · Ctrl+Alt+D", Icon = trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        var toggle = new Forms.ToolStripMenuItem("번역 활성", null, (_, _) => { enabled = !enabled; Cancel(); ClosePopup(); }) { Checked = true, CheckOnClick = true };
        menu.Items.Add(toggle); menu.Items.Add("설정", null, (_, _) => OpenSettings()); menu.Items.Add("종료", null, (_, _) => app.Shutdown()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => OpenSettings();
        hotkey.Pressed += () => _ = TranslateAsync(false);
        regionHotkey.Pressed += () => _ = TranslateLastRegionAsync();
        areaHotkey.Pressed += () => _ = TranslateAsync(true);
        arHotkey.Pressed += () => _ = TranslateArScreenAsync();
    }
    private FreeTranslationProvider NewProvider() => new();

    /// <summary>
    /// Applies the palette and keeps the OS-theme subscription in sync: only a "system" choice needs it,
    /// so any explicit light/dark pick detaches the handler instead of idling on every OS preference change.
    /// </summary>
    private void ApplyTheme(string? mode)
    {
        Theme.Apply(mode);
        if (string.Equals(Theme.Mode, "system", StringComparison.Ordinal)) AttachSystemTheme();
        else DetachSystemTheme();
    }

    private void AttachSystemTheme()
    {
        if (followingSystemTheme) return;
        followingSystemTheme = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void DetachSystemTheme()
    {
        if (!followingSystemTheme) return;
        followingSystemTheme = false;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    /// <summary>
    /// Raised on a SystemEvents thread. Unfrozen Freezables are thread-affine, so the palette swap has to
    /// hop to the UI thread before it touches a brush.
    /// </summary>
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (!string.Equals(settings.Theme, "system", StringComparison.Ordinal)) return;
        if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;
        app.Dispatcher.InvokeAsync(() => Theme.Apply("system"));
    }
    public void Start(bool background)
    {
        // Every hotkey is attempted even after a conflict: `||` short-circuited the rest, so one taken key
        // silently left the other three unregistered for the whole session.
        bool conflict = !hotkey.Set(settings.Modifiers, settings.Key);
        conflict |= !regionHotkey.Set(settings.RegionModifiers, settings.RegionKey);
        conflict |= !areaHotkey.Set(settings.AreaModifiers, settings.AreaKey);
        conflict |= !arHotkey.Set(settings.ArModifiers, settings.ArKey);
        if (conflict) { tray.ShowBalloonTip(5000, "훈수", "단축키 충돌입니다. 설정에서 다른 키를 지정하세요.", Forms.ToolTipIcon.Warning); OpenSettings(); }
        else if (!background) OpenSettings();
    }
    private void Cancel() { generation++; request?.Cancel(); CloseArOverlay(); }
    private void ClosePopup() { var old = popup; popup = null; old?.Close(); }
    private void CloseArOverlay() { var old = arOverlay; arOverlay = null; old?.Dismiss(); }
    public void Recover() { Cancel(); ClosePopup(); tray.ShowBalloonTip(3000, "훈수", "작업을 처리하지 못했습니다. 다시 시도하세요.", Forms.ToolTipIcon.Warning); }
    private void OpenSettings()
    {
        Cancel(); ClosePopup();
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, SaveSettings); settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show();
    }
    private string? SaveSettings(Settings next)
    {
        var previous = settings;
        if (!hotkey.Set(next.Modifiers, next.Key)) return "기본 단축키가 이미 사용 중입니다.";
        if (!regionHotkey.Set(next.RegionModifiers, next.RegionKey)) { hotkey.Set(previous.Modifiers, previous.Key); return "드래그 영역 단축키가 이미 사용 중입니다."; }
        if (!areaHotkey.Set(next.AreaModifiers, next.AreaKey)) { hotkey.Set(previous.Modifiers, previous.Key); regionHotkey.Set(previous.RegionModifiers, previous.RegionKey); return "영역 OCR 단축키가 이미 사용 중입니다."; }
        if (!arHotkey.Set(next.ArModifiers, next.ArKey)) { hotkey.Set(previous.Modifiers, previous.Key); regionHotkey.Set(previous.RegionModifiers, previous.RegionKey); areaHotkey.Set(previous.AreaModifiers, previous.AreaKey); return "전체 화면 AR 단축키가 이미 사용 중입니다."; }
        try
        {
            if (next.Startup != previous.Startup) SettingsStore.SetStartup(next.Startup);
            store.Save(next); settings = next.Copy(); ApplyTheme(settings.Theme); Cancel(); var oldProvider = provider; provider = NewProvider(); oldProvider.Dispose(); return null;
        }
        catch
        {
            hotkey.Set(previous.Modifiers, previous.Key);
            try { if (next.Startup != previous.Startup) SettingsStore.SetStartup(previous.Startup); } catch { }
            return "설정을 저장하지 못했습니다. 폴더 접근 권한을 확인하세요.";
        }
    }
    internal async Task TranslateAsync(bool manual)
    {
        Cancel(); ClosePopup();
        if (!enabled) return;
        var local = new CancellationTokenSource(); request = local; var token = local.Token; long id = generation;
        // Set when this run came from a drag; keeps the on-screen plate in step with the popup.
        Rectangle? overlayRegion = null;
        try
        {
            string? identity;
            Native.Point point;
            if (manual)
            {
                // A drag deliberately reads nothing under the cursor, so the own-process guard below must not
                // apply to it. While it did, an open translation popup left our own window under the cursor,
                // ProcessAt named hoonsoo.exe and this run returned before the selector was ever shown — which
                // is why dragging a region with the popup open did nothing at all, without a word.
                Native.GetCursorPos(out point);
                identity = null;
            }
            else
            {
                await Task.Delay(80, token); Native.GetCursorPos(out point);
                identity = Native.ProcessAt(point);
                if (identity is not null && SelfIdentities.Contains(identity)) return;
            }
            if (manual && !settings.Ocr) { ReportMessage(point, "설정에서 OCR fallback을 켜 주세요."); return; }
            CaptureResult? result;
            if (manual)
            {
                var (selected, outcome) = await RegionSelector.SelectDetailedAsync(token);
                if (selected is not { } r) { ReportEndedSelection(outcome); return; }
                await Task.Delay(100, token);
                // Recheck after selection windows disappear, including each sampled app under the region.
                lastRegion = r;
                overlayRegion = r;
                sourceRegion = r;
                plateRect = null;
                point = new Native.Point(r.Left, r.Top);
                // The plate goes on the screen itself, not only inside the popup: it is up as soon as the drag
                // ends and stays anchored under the region while OCR and translation run. ShowRegionOverlay is
                // a no-op when the settings answer in the popup only.
                ShowRegionOverlay(r, "영역에서 텍스트를 읽는 중…", true);
                result = await capture.RunAsync("--ocr", token, r.X, r.Y, r.Width, r.Height);
            }
            else
            {
                // No popup before capture: OCR must never capture our own loading surface.
                // The cursor flow knows the OCR window it reads, so the popup can step around it too.
                sourceRegion = CaptureClient.Around(point);
                plateRect = null;
                result = await CapturePipeline.RunAsync(
                    t => SameTarget(point, identity) ? capture.RunAsync("--uia", t, point.X, point.Y) : Task.FromResult<CaptureResult?>(null),
                    t => { var r = CaptureClient.Around(point); return SameTarget(point, identity) ? capture.RunAsync("--ocr", t, r.X, r.Y, r.Width, r.Height) : Task.FromResult<CaptureResult?>(null); }, settings.Ocr, token);
            }
            token.ThrowIfCancellationRequested(); if (id != generation) return;
            if (!SameTarget(point, manual ? Native.ProcessAt(point) : identity)) { CloseArOverlay(); return; }
            // The hover flow only has the popup to answer in, so the display-mode setting (which is about a
            // dragged region) must not silence it; only a drag may be answered on the screen alone.
            if (!manual || RegionResultModes.ShowsPopup(settings.RegionResult)) Show(point);
            if (result is null || !result.Quality || TextQuality.Clean(result.Text).Length == 0)
            {
                if (overlayRegion is { } missed) ShowRegionOverlay(missed, settings.Ocr ? "텍스트를 정확하게 읽지 못했습니다." : "OCR이 꺼져 있습니다.", false);
                popup?.Error(settings.Ocr ? "텍스트를 읽지 못했습니다. 대상 창이 관리자 권한/보안 보호 중이면 훈수를 관리자 권한으로 실행하거나 ‘영역 OCR’을 드래그하세요." : "텍스트를 읽지 못했습니다. 다시 읽거나 설정에서 OCR을 켜 주세요.", settings); return;
            }
            popup?.Status("번역 중…");
            if (overlayRegion is { } busy) ShowRegionOverlay(busy, "번역 중…", true);
            var translated = await provider.TranslateAsync(result.Text, result.Method, settings.Terms, settings.ProtectCode, token);
            token.ThrowIfCancellationRequested();
            if (id == generation)
            {
                popup?.Result(translated, settings);
                if (overlayRegion is { } done) ShowRegionOverlay(done, translated.Error ?? translated.TranslatedText, false);
            }
        }
        catch (OperationCanceledException) { }
        catch { if (id == generation) { popup?.Error("캡처 또는 번역에 실패했습니다. 다시 읽기를 눌러 주세요.", settings); if (overlayRegion is { } failed) ShowRegionOverlay(failed, "텍스트를 읽지 못했습니다.", false); } }
        finally { if (ReferenceEquals(request, local)) request = null; local.Dispose(); }
    }
    private async Task TranslateLastRegionAsync()
    {
        if (lastRegion is not { } region) { await TranslateAsync(true); return; }
        Cancel(); ClosePopup();
        if (!enabled) return;
        var local = new CancellationTokenSource(); request = local; var token = local.Token; var point = new Native.Point(region.Left, region.Top);
        sourceRegion = region; plateRect = null;
        try
        {
            ShowRegionOverlay(region, "영역에서 텍스트를 읽는 중…", true);
            var result = await capture.RunAsync("--ocr", token, region.X, region.Y, region.Width, region.Height);
            token.ThrowIfCancellationRequested();
            if (RegionResultModes.ShowsPopup(settings.RegionResult)) Show(point);
            if (result is null || !result.Quality || TextQuality.Clean(result.Text).Length == 0) { ShowRegionOverlay(region, "텍스트를 정확하게 읽지 못했습니다.", false); popup?.Error("저장된 드래그 영역에서 텍스트를 읽지 못했습니다.", settings); return; }
            popup?.Status("번역 중…"); ShowRegionOverlay(region, "번역 중…", true);
            var translated = await provider.TranslateAsync(result.Text, result.Method, settings.Terms, settings.ProtectCode, token);
            popup?.Result(translated, settings, settings.RegionTextScale); ShowRegionOverlay(region, translated.Error ?? translated.TranslatedText, false);
        }
        catch (OperationCanceledException) { }
        catch { popup?.Error("저장된 영역 OCR에 실패했습니다. 영역 OCR로 다시 지정하세요.", settings); ShowRegionOverlay(region, "텍스트를 읽지 못했습니다.", false); }
        finally { if (ReferenceEquals(request, local)) request = null; local.Dispose(); }
    }
    private bool SameTarget(Native.Point point, string? identity) => enabled && Native.ProcessAt(point) == identity;
    private bool RegionAllowed(Rectangle r)
    {
        if (r.Width < 8 || r.Height < 8) return false;
        for (int y = r.Top + 1; y < r.Bottom; y += Math.Max(1, r.Height / 6))
            for (int x = r.Left + 1; x < r.Right; x += Math.Max(1, r.Width / 8))
                _ = Native.ProcessAt(new Native.Point(x, y));
        return true;
    }
    /// <summary>
    /// Shows a dragged-area translation on the screen itself (AR style) when the settings ask for it. One
    /// overlay serves both region flows; it is rebuilt when the region lands on another monitor so the plate
    /// is always measured against the bounds it is drawn into. A no-op in "popup" mode, where the answer
    /// belongs to the popup alone — and the three callers below then have nothing to say to the screen.
    /// </summary>
    private void ShowRegionOverlay(Rectangle region, string text, bool pending)
    {
        if (!RegionResultModes.ShowsOverlay(settings.RegionResult)) return;
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(region.Left + region.Width / 2, region.Top + region.Height / 2));
        if (arOverlay is null || arOverlay.ScreenBounds != screen.Bounds)
        {
            CloseArOverlay();
            // Same guard as the full-screen flow: a dismissed overlay must not clear a newer reference.
            ArOverlayWindow? overlay = null;
            overlay = new ArOverlayWindow(screen.Bounds, () => { if (ReferenceEquals(arOverlay, overlay)) arOverlay = null; });
            arOverlay = overlay;
            overlay.Show();
        }
        plateRect = arOverlay.DisplayRegionResult(region, text, pending, settings.RegionTextScale);
    }

    /// <summary>
    /// Says something on whichever surface the settings enable. The plate needs a region to hang off, so a
    /// message that arrives before one exists falls back to the tray balloon rather than going unsaid.
    /// </summary>
    private void ReportMessage(Native.Point point, string message)
    {
        if (RegionResultModes.ShowsPopup(settings.RegionResult)) { Show(point); popup!.Error(message, settings); return; }
        tray.ShowBalloonTip(4000, "훈수", message, Forms.ToolTipIcon.Warning);
    }

    /// <summary>
    /// A drag that ends without a usable rectangle used to return in silence, which reads exactly like the
    /// region selection never opening. Cancelling on purpose still needs no message; the rest do.
    /// </summary>
    private void ReportEndedSelection(RegionSelectionOutcome outcome)
    {
        if (RegionSelection.Guidance(outcome) is not { } message) return;
        Native.GetCursorPos(out var point);
        ReportMessage(point, message);
    }

    private PopupWindow Show(Native.Point point)
    {
        var p = new PopupWindow(); popup = p;
        p.Reread += () => _ = TranslateLastRegionAsync(); p.SelectRegion += () => _ = TranslateAsync(true); p.SettingsRequested += OpenSettings;
        p.Dismissed += () => { if (ReferenceEquals(popup, p)) { popup = null; Cancel(); } };
        var avoid = new List<Rectangle>();
        if (sourceRegion is { } source) avoid.Add(source);
        if (plateRect is { } plate) avoid.Add(plate);
        // The window opens beside the captured text instead of over it.
        p.Present(point, settings, avoid);
        return p;
    }
    internal async Task TranslateArScreenAsync()
    {
        if (arOverlay is not null)
        {
            // Closing the HUD must stop the OCR/translation it started too: without the cancel, a stale run
            // finished after the next one began and closed the new overlay from under the user.
            Cancel();
            return;
        }
        Cancel(); ClosePopup();
        if (!enabled) return;
        var local = new CancellationTokenSource(); request = local; var token = local.Token; long id = generation;
        try
        {
            Native.GetCursorPos(out var cursorPos);
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(cursorPos.X, cursorPos.Y));
            var bounds = screen.Bounds;

            // Target the active foreground window first, clipping to the current screen
            var foregroundHwnd = Native.GetForegroundWindow();
            if (foregroundHwnd != IntPtr.Zero && Native.GetWindowRect(foregroundHwnd, out var winRect))
            {
                var rect = new Rectangle(winRect.Left, winRect.Top, Math.Max(0, winRect.Right - winRect.Left), Math.Max(0, winRect.Bottom - winRect.Top));
                var clipped = Rectangle.Intersect(screen.Bounds, rect);
                if (clipped.Width >= 120 && clipped.Height >= 120)
                {
                    bounds = clipped;
                }
            }

            // A dismissed overlay must only clear its own reference: a stale one used to null the newer HUD.
            ArOverlayWindow? overlay = null;
            overlay = new ArOverlayWindow(bounds, () => { if (ReferenceEquals(arOverlay, overlay)) arOverlay = null; });
            arOverlay = overlay;
            overlay.Show();
            overlay.ShowLoading("⚡ 활성 창 훈수 자막 분석 중...");
            var pageResult = await WindowsMediaOcr.CaptureAndRecognizeAsync(bounds, token);
            if (pageResult is null || pageResult.Lines.Count == 0)
            {
                if (pageResult?.IsBlackScreen == true)
                {
                    arOverlay?.ShowLoading("🛡️ DRM/보안 보호 또는 전체화면 게임으로 화면이 가려져 있습니다. (ESC로 닫기)");
                }
                else
                {
                    arOverlay?.ShowLoading("인식된 텍스트가 없습니다. (ESC로 닫기)");
                }
                await Task.Delay(2000, token);
                CloseArOverlay();
                return;
            }

            var blocks = ArTranslationService.MergeLinesToBlocks(pageResult.Lines);
            if (blocks.Count == 0)
            {
                arOverlay?.ShowLoading("번역할 영문 텍스트가 없습니다. (ESC로 닫기)");
                await Task.Delay(1500, token);
                CloseArOverlay();
                return;
            }

            arOverlay?.ShowLoading($"⚡ 훈수 번역 중 ({blocks.Count}개 블록)...");
            // The screen is full of source code more often than not, so the code-protection setting applies
            // here exactly as it does to a cursor popup.
            var outcome = await ArTranslationService.TranslateBlocksAsync(blocks, settings.ProtectCode, token);

            int translatedCount = blocks.Count(b => !string.IsNullOrWhiteSpace(b.TranslatedText));
            if (translatedCount == 0)
            {
                if (FreeTranslationProvider.IsGoogleRateLimited)
                {
                    arOverlay?.ShowLoading("⚠️ Google 번역 한도에 도달했습니다. 잠시 후 다시 시도하세요. (ESC로 닫기)");
                }
                else if (outcome.TransportFailed)
                {
                    // "Nothing to translate" used to cover an unreachable service, which sends the user
                    // looking at the wrong problem.
                    arOverlay?.ShowLoading("번역 서비스에 연결하지 못했습니다. 인터넷 연결을 확인하세요. (ESC로 닫기)");
                }
                else
                {
                    arOverlay?.ShowLoading("번역할 내용이 없습니다. (ESC로 닫기)");
                }
                await Task.Delay(2000, token);
                CloseArOverlay();
                return;
            }

            if (arOverlay is not null && id == generation && !token.IsCancellationRequested)
            {
                arOverlay.DisplaySubtitles(blocks, bounds.Left, bounds.Top, settings.ArTextScale);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Only the run that is still current may tear down what is on screen.
            if (id != generation) return;
            CloseArOverlay();
            tray.ShowBalloonTip(3000, "훈수", "전체 화면 번역을 수행하지 못했습니다.", Forms.ToolTipIcon.Warning);
        }
        finally { if (ReferenceEquals(request, local)) request = null; local.Dispose(); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        DetachSystemTheme();
        Cancel(); ClosePopup(); CloseArOverlay(); settingsWindow?.Close(); hotkey.Dispose(); regionHotkey.Dispose(); areaHotkey.Dispose(); arHotkey.Dispose(); tray.Visible = false; tray.Dispose(); provider.Dispose();
    }
}


