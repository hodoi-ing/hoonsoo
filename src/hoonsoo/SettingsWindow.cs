using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hoonsoo;

public sealed class SettingsWindow : Window
{
    private readonly Settings draft;
    private readonly TextBlock status = Ui.Text("", 13, false);
    private readonly CancellationTokenSource lifetime = new();

    public SettingsWindow(Settings current, Func<Settings, string?> save)
    {
        draft = current.Copy();
        Title = "훈수 설정";
        Width = 840;
        // 760 으로는 영역 번역 표시 카드가 추가된 만큼 모자라서, 새 설정이 스크롤 아래로 밀린다.
        // 810 은 1080p 125% 배율(가용 약 1032px)에서도 화면 안에 들어온다.
        Height = 810;
        MinWidth = 760;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.BrushBgApp;
        FontFamily = Ui.AppFont;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        // Header Section (48px 큼직하고 선명한 로고)
        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 14) };
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        brandRow.Children.Add(Ui.LogoImage(48));
        
        var titleGroup = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        titleGroup.Children.Add(new TextBlock
        {
            Text = "훈수 (Hoonsoo)",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.BrushTextPrimary
        });
        titleGroup.Children.Add(Ui.Text("영어 위에서 단축키를 누르면 번역합니다. 기본값: Ctrl + Alt + D", 12.5, true));
        brandRow.Children.Add(titleGroup);
        header.Children.Add(brandRow);

        // ==========================================
        // 1. [일반] 탭: 좌우 2열 그리드 레이아웃
        // ==========================================
        var generalGrid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        generalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        generalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); // 간격
        generalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // [좌측 열: 단축키 설정]
        var leftCol = new StackPanel();
        Grid.SetColumn(leftCol, 0);

        leftCol.Children.Add(Ui.Heading("전역 단축키"));
        leftCol.Children.Add(Ui.Text("클릭 후 Ctrl, Alt, Shift 또는 Win 조합을 누르세요.", 12, true));

        var hotkeyCards = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        TextBox AddHotkey(string title, string description, uint modifiers, uint key, Action<uint, uint> assign)
        {
            var cardContent = new StackPanel();
            cardContent.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Theme.BrushTextPrimary,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
            cardContent.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.BrushTextSecondary,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var box = new TextBox { IsReadOnly = true, Text = HotkeyLabel(modifiers, key), Visibility = Visibility.Collapsed };

            var hotkeySlot = new Border
            {
                Background = Theme.BrushBgInput,
                BorderBrush = Theme.BrushHairlineStrong,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Radii.Control),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 6, 0, 0),
                Focusable = true,
                Cursor = Cursors.Hand
            };

            var slotDock = new DockPanel { LastChildFill = true };
            var keyHint = new TextBlock
            {
                Text = "클릭 후 키 입력",
                Foreground = Theme.BrushTextMuted,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(keyHint, Dock.Right);
            slotDock.Children.Add(keyHint);

            var keyList = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            slotDock.Children.Add(keyList);
            hotkeySlot.Child = slotDock;

            void RefreshKeycaps(uint m, uint k)
            {
                keyList.Children.Clear();
                var label = HotkeyLabel(m, k);
                var parts = label.Split(new[] { " + " }, StringSplitOptions.None);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        keyList.Children.Add(new TextBlock
                        {
                            Text = "+",
                            Foreground = Theme.BrushTextMuted,
                            FontSize = 11,
                            FontWeight = FontWeights.Bold,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(2, 0, 4, 0)
                        });
                    }
                    keyList.Children.Add(Ui.Keycap(parts[i], isSmall: true));
                }
            }

            RefreshKeycaps(modifiers, key);

            hotkeySlot.GotFocus += (_, _) =>
            {
                hotkeySlot.BorderBrush = Theme.BrushAccent;
                hotkeySlot.BorderThickness = new Thickness(1.5);
                keyHint.Text = "새 단축키를 누르세요...";
                keyHint.Foreground = Theme.BrushAccent;
            };

            hotkeySlot.LostFocus += (_, _) =>
            {
                hotkeySlot.BorderBrush = Theme.BrushHairlineStrong;
                hotkeySlot.BorderThickness = new Thickness(1);
                keyHint.Text = "클릭 후 키 입력";
                keyHint.Foreground = Theme.BrushTextMuted;
            };

            hotkeySlot.MouseDown += (_, _) => hotkeySlot.Focus();

            hotkeySlot.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
                var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(pressedKey);
                var modifiersPressed = Keyboard.Modifiers;
                if (!Hotkey.IsValid(1, virtualKey) || modifiersPressed == ModifierKeys.None)
                {
                    status.Text = "Ctrl, Alt, Shift 또는 Win을 포함한 단축키를 입력하세요.";
                    status.Foreground = Theme.BrushDanger;
                    return;
                }

                var modifierBits = ((modifiersPressed & ModifierKeys.Alt) != 0 ? 1u : 0)
                    | ((modifiersPressed & ModifierKeys.Control) != 0 ? 2u : 0)
                    | ((modifiersPressed & ModifierKeys.Shift) != 0 ? 4u : 0)
                    | ((modifiersPressed & ModifierKeys.Windows) != 0 ? 8u : 0);
                assign(modifierBits, virtualKey);
                box.Text = HotkeyLabel(modifierBits, virtualKey);
                RefreshKeycaps(modifierBits, virtualKey);
                status.Text = "단축키를 변경했습니다. 하단 저장을 눌러 적용하세요.";
                status.Foreground = Theme.BrushTextSecondary;
            };
            cardContent.Children.Add(hotkeySlot);

            hotkeyCards.Children.Add(new Border
            {
                Background = Theme.BrushBgCard,
                BorderBrush = Theme.BrushHairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Radii.Card),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = cardContent
            });
            return box;
        }

        AddHotkey("기본 번역", "커서 아래 영어를 번역 팝업으로 엽니다.", draft.Modifiers, draft.Key, (m, k) => { draft.Modifiers = m; draft.Key = k; });
        AddHotkey("마지막 드래그 재번역", "이전에 선택한 OCR 영역을 다시 번역합니다.", draft.RegionModifiers, draft.RegionKey, (m, k) => { draft.RegionModifiers = m; draft.RegionKey = k; });
        AddHotkey("새 영역 드래그", "화면에서 새 OCR 영역을 지정해 번역합니다.", draft.AreaModifiers, draft.AreaKey, (m, k) => { draft.AreaModifiers = m; draft.AreaKey = k; });
        AddHotkey("전체 화면 AR 자막", "화면 전체 영문을 감지하여 1:1 자막 HUD로 띄웁니다.", draft.ArModifiers, draft.ArKey, (m, k) => { draft.ArModifiers = m; draft.ArKey = k; });
        leftCol.Children.Add(hotkeyCards);
        generalGrid.Children.Add(leftCol);

        // [우측 열: 동작 및 옵션 설정]
        var rightCol = new StackPanel();
        Grid.SetColumn(rightCol, 2);

        // 영역 번역 표시 카드 — 표시 위치와 화면 표시 크기를 한 카드에 모아 열 맨 위에 둔다.
        // 창 아래쪽에 두면 스크롤해야 보이므로, 새로 추가한 설정이 먼저 눈에 들어오게 한다.
        rightCol.Children.Add(Ui.Heading("영역 번역 표시"));
        rightCol.Children.Add(Ui.Text("드래그한 영역의 번역을 어디에, 얼마나 크게 보여줄지 정합니다. 기본 100%.", 12, true));

        var regionCardContent = new StackPanel();

        DockPanel CreatePickerRow(string labelText, ComboBox pick)
        {
            pick.Width = 150;
            pick.MinHeight = 32;
            pick.Margin = new Thickness(8, 0, 0, 0);
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
            row.Children.Add(new TextBlock
            {
                Text = labelText,
                Foreground = Theme.BrushTextSecondary,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            });
            return row;
        }

        var regionResultPick = new ComboBox();
        Ui.StyleComboBox(regionResultPick);
        regionResultPick.Items.Add("화면 위 오버레이");
        regionResultPick.Items.Add("번역 팝업 창");
        regionResultPick.Items.Add("둘 다 (기본)");
        regionResultPick.SelectedIndex = RegionResultModes.Normalize(draft.RegionResult) switch
        {
            RegionResultModes.Overlay => 0,
            RegionResultModes.Popup => 1,
            _ => 2
        };
        regionResultPick.SelectionChanged += (_, _) =>
        {
            draft.RegionResult = regionResultPick.SelectedIndex switch
            {
                0 => RegionResultModes.Overlay,
                1 => RegionResultModes.Popup,
                _ => RegionResultModes.Both
            };
        };
        regionCardContent.Children.Add(CreatePickerRow("표시 위치", regionResultPick));

        // 두 배율은 한 줄에 나란히 둔다: 카드가 세 줄이 되면 설정 창이 새 설정을 스크롤 아래로 밀어낸다.
        var scaleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var arPicker = CreatePercentPicker(draft.ArTextPercent, v => draft.ArTextPercent = v);
        arPicker.Width = 80;
        arPicker.Margin = new Thickness(8, 0, 18, 0);
        var regionPicker = CreatePercentPicker(draft.RegionTextPercent, v => draft.RegionTextPercent = v);
        regionPicker.Width = 80;
        regionPicker.Margin = new Thickness(8, 0, 0, 0);
        scaleRow.Children.Add(new TextBlock
        {
            Text = "전체 자막",
            Foreground = Theme.BrushTextSecondary,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        scaleRow.Children.Add(arPicker);
        scaleRow.Children.Add(new TextBlock
        {
            Text = "영역 번역",
            Foreground = Theme.BrushTextSecondary,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        scaleRow.Children.Add(regionPicker);
        regionCardContent.Children.Add(scaleRow);

        ComboBox CreatePercentPicker(int currentPercent, Action<int> onPercentChanged)
        {
            var pick = new ComboBox();
            Ui.StyleComboBox(pick);
            int snapped = Settings.SnapTextPercent(currentPercent);
            int selectedIdx = 0;
            for (int i = 0; i < Settings.TextPercentSteps.Length; i++)
            {
                pick.Items.Add($"{Settings.TextPercentSteps[i]}%");
                if (Settings.TextPercentSteps[i] == snapped) selectedIdx = i;
            }
            pick.SelectedIndex = selectedIdx;
            pick.SelectionChanged += (_, _) =>
            {
                if (pick.SelectedIndex >= 0 && pick.SelectedIndex < Settings.TextPercentSteps.Length)
                {
                    onPercentChanged(Settings.TextPercentSteps[pick.SelectedIndex]);
                }
            };
            return pick;
        }

        rightCol.Children.Add(new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Card),
            Padding = new Thickness(16, 11, 16, 11),
            Margin = new Thickness(0, 2, 0, 12),
            Child = regionCardContent
        });

        rightCol.Children.Add(Ui.Heading("화면 테마"));
        rightCol.Children.Add(Ui.Text("고르면 바로 미리보기됩니다. 유지하려면 하단 저장을 누르세요.", 12, true));

        var themePick = new ComboBox();
        Ui.StyleComboBox(themePick);
        themePick.Items.Add("시스템 설정 따르기");
        themePick.Items.Add("라이트");
        themePick.Items.Add("다크");
        themePick.SelectedIndex = draft.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        themePick.SelectionChanged += (_, _) =>
        {
            draft.Theme = themePick.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
            Theme.Apply(draft.Theme);
        };

        rightCol.Children.Add(new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Card),
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 4, 0, 16),
            Child = themePick
        });

        rightCol.Children.Add(Ui.Heading("동작 옵션"));
        rightCol.Children.Add(Ui.Text("앱의 번역 및 실행 동작을 제어합니다.", 12, true));

        var optionsCardContent = new StackPanel();
        AddCheck(optionsCardContent, "개발용어 목록 표시 (번역 결과에 용어와 한국어명)", draft.Terms, v => draft.Terms = v);
        AddCheck(optionsCardContent, "코드·명령·경로 보호 (원문 보존)", draft.ProtectCode, v => draft.ProtectCode = v);
        AddCheck(optionsCardContent, "로컬 OCR fallback 지원", draft.Ocr, v => draft.Ocr = v);
        AddCheck(optionsCardContent, "팝업 자동 닫기 (마우스 호버 시 연장)", draft.AutoClose, v => draft.AutoClose = v);

        // 자동 닫기 시간 입력 행
        var autoCloseRow = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        var seconds = new TextBox { Text = draft.CloseSeconds.ToString(), Width = 64, MinHeight = 28 };
        Ui.StyleTextBox(seconds);
        seconds.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(seconds, Dock.Right);
        autoCloseRow.Children.Add(seconds);
        autoCloseRow.Children.Add(new TextBlock
        {
            Text = "자동 닫기 시간 (3~300초):",
            Foreground = Theme.BrushTextSecondary,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        optionsCardContent.Children.Add(autoCloseRow);

        AddCheck(optionsCardContent, "Windows 시작 시 자동 실행", draft.Startup, v => draft.Startup = v);

        var optionsCard = new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Card),
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 4, 0, 16),
            Child = optionsCardContent
        };
        rightCol.Children.Add(optionsCard);

        generalGrid.Children.Add(rightCol);


        // One page now that the API tab is gone, so the general panel is the window body: a TabControl
        // with a single tab would only add chrome above content that already scrolls.
        var content = Scroll(generalGrid);

        // 하단 고정 액션 바 (저장 버튼 및 상태 메시지)
        var saveButton = Ui.Button("저장", (_, _) =>
        {
            if (!int.TryParse(seconds.Text, out var n) || n is < 3 or > 300)
            {
                status.Text = "자동 닫기는 3~300초입니다.";
                status.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                return;
            }
            draft.CloseSeconds = n;
            var error = save(draft);
            if (error is null)
            {
                status.Text = "설정을 저장했습니다.";
                status.Foreground = Theme.BrushAccent;
            }
            else
            {
                status.Text = error;
                status.Foreground = Theme.BrushDanger;
            }
        }, true);
        saveButton.MinWidth = 92;
        saveButton.MinHeight = 34;
        saveButton.Margin = new Thickness(12, 0, 0, 0);

        var footerContent = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(saveButton, Dock.Right);
        footerContent.Children.Add(saveButton);
        status.Margin = new Thickness(0);
        status.VerticalAlignment = VerticalAlignment.Center;
        footerContent.Children.Add(status);

        var footer = new Border
        {
            Background = Theme.BrushBgCard,
            BorderBrush = Theme.BrushHairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12, 24, 12),
            Child = footerContent
        };

        var mainLayout = new DockPanel();
        mainLayout.Children.Add(header);
        DockPanel.SetDock(header, Dock.Top);
        var headerDivider = new Border { Height = 1, Background = Theme.BrushHairline };
        DockPanel.SetDock(headerDivider, Dock.Top);
        mainLayout.Children.Add(headerDivider);
        DockPanel.SetDock(footer, Dock.Bottom);
        mainLayout.Children.Add(footer);
        mainLayout.Children.Add(content);

        Content = mainLayout;

        // Entrance: the shell fades in and its three regions settle in with a short stagger.
        Motion.EnterWindow(this, Motion.Base);
        Loaded += (_, _) =>
        {
            Motion.FadeSlideIn(header, 8, Motion.Base);
            Motion.FadeSlideIn(footer, -8, Motion.Base, 40);
            Motion.FadeSlideIn(content, 8, Motion.Base, 80);
        };
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    /// <summary>
    /// Tab panes scroll rather than clip. At 125-150% display scaling the four hotkey cards are taller
    /// than any window the user will keep on screen, and the footer must stay pinned below the scroll
    /// area instead of scrolling away with it.
    /// </summary>
    private static ScrollViewer Scroll(FrameworkElement content)
    {
        var sv = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        Ui.StyleScrollViewer(sv);
        return sv;
    }

    private static void AddCheck(Panel panel, string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        Ui.StyleCheckBox(c);
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        panel.Children.Add(c);
    }

    private static string HotkeyLabel(uint modifiers, uint key) => ((modifiers & 2) != 0 ? "Ctrl + " : "") + ((modifiers & 1) != 0 ? "Alt + " : "") + ((modifiers & 4) != 0 ? "Shift + " : "") + ((modifiers & 8) != 0 ? "Win + " : "") + KeyInterop.KeyFromVirtualKey((int)key);
}
