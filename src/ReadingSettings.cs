using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    // Same hover and pointer-away behavior as the original controller, with a configurable deadline.
    internal sealed class BubbleLifetime
    {
        private DateTime shownAt, awayAt;
        private int seconds;
        internal static int DisplaySeconds(int configuredSeconds, int automaticSeconds, bool translated)
        { return translated && configuredSeconds > 0 ? configuredSeconds : automaticSeconds; }
        internal void Start(DateTime now, int displaySeconds)
        { shownAt = now; awayAt = DateTime.MinValue; seconds = displaySeconds; }
        internal void Reset() { awayAt = DateTime.MinValue; }
        internal bool ShouldDismiss(DateTime now, bool near, bool over)
        {
            if (over) { shownAt = now; awayAt = DateTime.MinValue; return false; }
            if (!near)
            {
                if (awayAt == DateTime.MinValue) awayAt = now;
                if ((now - awayAt).TotalMilliseconds > 550 && (now - shownAt).TotalMilliseconds > 900) return true;
            }
            else awayAt = DateTime.MinValue;
            return (now - shownAt).TotalSeconds > seconds;
        }
    }

    internal static class BubbleAppearance
    {
        internal static FontFamily ResolveFont(string name)
        {
            string requested = (name ?? "").Trim();
            return Fonts.SystemFontFamilies.FirstOrDefault(f => string.Equals(f.Source, requested, StringComparison.OrdinalIgnoreCase))
                ?? new FontFamily("Microsoft YaHei UI");
        }
        internal static bool TryParseBackground(string text, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            int rgb;
            if (value.Length != 7 || value[0] != '#' || !value.Substring(1).All(Uri.IsHexDigit) || !int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        internal static Color Background(string text)
        {
            Color color;
            if (TryParseBackground(text, out color)) return color;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0) return Color.FromRgb(37, 40, 47);
            }
            catch { }
            return Color.FromRgb(252, 252, 253);
        }
        private static double Channel(byte value)
        {
            double channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        internal static double Luminance(Color color)
        { return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B); }
        internal static double Contrast(Color first, Color second)
        {
            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
        internal static Color Foreground(Color background)
        {
            var dark = Color.FromRgb(36, 40, 48);
            var light = Color.FromRgb(237, 239, 243);
            Color preferred = Luminance(background) < 0.35 ? light : dark;
            if (Contrast(background, preferred) >= 4.5) return preferred;
            // Opaque RGB backgrounds guarantee that black or white reaches at least 4.5:1.
            return Contrast(background, Colors.Black) >= Contrast(background, Colors.White) ? Colors.Black : Colors.White;
        }
        internal static Color Border(Color background)
        { return Luminance(background) < 0.35 ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(35, 40, 48, 64); }
    }

    internal sealed class SettingsWindow : Window
    {
        private const string Automatic = "自动（按译文长度）";
        private static readonly string[] Languages = {
            "简体中文 · zh-Hans", "繁体中文 · zh-Hant", "英语 · en", "日语 · ja", "韩语 · ko",
            "法语 · fr", "德语 · de", "西班牙语 · es", "俄语 · ru"
        };
        private readonly Settings settings;
        private readonly Action saved;
        private readonly IStartupRegistration startup;
        private readonly IDeepLCredentials credentials;
        private readonly PasswordBox deepLKey;
        private readonly TextBlock deepLStatus;
        private bool clearDeepLKey;
        private readonly CheckBox enabled, clipboard, allowOverwrite, autoStart;
        private readonly Func<bool> confirmOverwrite;
        private bool fillingOverwrite;
        private readonly ComboBox language, provider, indexModel, fontFamily, fontSize, autoHide;
        private readonly TextBox maxSelection, excludedApps, background, englishPercent;
        private readonly TextBlock error, previewText, startupStatus;
        private readonly Border preview;
        private readonly Grid layout;
        private readonly TabControl tabs;
        private bool refreshingStartup, startupDirty;
        private bool? observedStartup;

        internal SettingsWindow(Settings settings, Action saved, IStartupRegistration startup = null, IDeepLCredentials credentials = null,
            Func<bool> confirmOverwrite = null)
        {
            this.settings = settings; this.saved = saved;
            this.confirmOverwrite = confirmOverwrite ?? delegate { return new ClipboardOverwriteConfirmation(this).ShowDialog() == true; };
            this.startup = startup ?? StartupRegistration.CurrentUser();
            this.credentials = credentials ?? DeepLCredentials.Current();
            Title = "轻译 · 设置";
            Width = 620; Height = 790; MinWidth = 480; MinHeight = 450;
            MaxHeight = Math.Max(450, SystemParameters.WorkArea.Height - 40);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            Background = Brushes.White;
            layout = new Grid { Background = Brushes.White };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = layout;
            var header = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
            header.Children.Add(new TextBlock { Text = "轻译设置", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Ink() });
            header.Children.Add(Help("保存后生效；取消保留当前配置和开机自启状态。"));
            layout.Children.Add(header);
            tabs = new TabControl { Margin = new Thickness(20, 0, 20, 0) };
            Grid.SetRow(tabs, 1); layout.Children.Add(tabs);

            var translation = NewTab("翻译");
            enabled = Toggle("自动划词翻译");
            clipboard = Toggle("启用选区取词");
            translation.Children.Add(enabled); translation.Children.Add(clipboard);
            translation.Children.Add(Help("托盘开关立即生效；面板修改在保存后生效。关闭取词后停止划词翻译。先尝试隔离读取选区；需要临时复制时，能完整备份则恢复原剪贴板。无法备份时按下方覆盖选项处理。"));
            allowOverwrite = Toggle("取词优先，允许覆盖（默认关闭）");
            translation.Children.Add(allowOverwrite);
            translation.Children.Add(Help("仅在无法完整备份时允许覆盖原剪贴板。" + ClipboardBackupException.Risk + "关闭时，可在失败提示中单次选择“本次覆盖并取词”。"));
            AutomationProperties.SetHelpText(allowOverwrite, ClipboardBackupException.Risk);
            allowOverwrite.Checked += delegate {
                if (fillingOverwrite) return;
                bool approved = false;
                try { approved = this.confirmOverwrite(); }
                catch { if (error != null) Invalid("无法完成覆盖风险确认，选项保持关闭。", allowOverwrite, 0); }
                if (!approved) allowOverwrite.IsChecked = false;
            };
            provider = new ComboBox { ItemsSource = new[] { "自动（微软失败后腾讯）", "仅微软", "仅腾讯（中文）", "仅 DeepL（自备 API 密钥）", "仅哔哩哔哩 Index" }, MinHeight = 32 };
            AddField(translation, "在线翻译服务", provider);
            translation.Children.Add(Help("自动模式先查本地英汉词库，再尝试微软、腾讯；指定服务会跳过词库且不自动切换。DeepL 须在 DeepL 页保存密钥；自动模式不会调用 DeepL。"));
            indexModel = new ComboBox { ItemsSource = IndexTranslator.Models, MinHeight = 32 };
            AddField(translation, "Index 模型", indexModel);
            translation.Children.Add(Help("仅 Index 使用：2B / 9B / 35B。选中文字会直发哔哩哔哩公开接口，无需密钥；不自动换模型。三档见官方文档，在线可用性以实际请求为准。"));
            language = new ComboBox { IsEditable = true, ItemsSource = Languages, MinHeight = 32 };
            AddField(translation, "目标语言", language);
            translation.Children.Add(Help("可直接输入服务支持的语言代码，如 en、ja。Index 按 zh-Hans / zh-Hant 指定简体 / 繁體中文；DeepL 对应 ZH-HANS / ZH-HANT。词库和腾讯固定输出中文。"));
            maxSelection = Input();
            AddField(translation, "选区字符数上限", maxSelection);
            translation.Children.Add(Help("正整数，默认 2,000。超过上限只提示分段选择，不发送该选区。"));
            excludedApps = new TextBox { AcceptsReturn = true, MinHeight = 82, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AddField(translation, "排除应用", excludedApps);
            translation.Children.Add(Help("每行一个进程名，也可用逗号或分号分隔，如 SomeApp 或 SomeApp.exe；不要填写路径。"));
            englishPercent = Input();
            AddField(translation, "最低英文占比（%）", englishPercent);
            translation.Children.Add(Help("0–100%。A–Z/a–z 字母数 ÷ 全部文字字母数；中文及其他语言计入分母，标点、空白、数字不计。Hello中文约为 71%。"));
            translation.Children.Add(Help("0% 保留原语言过滤；纯中文和没有字母的选区仍跳过。大于 0% 时必须含 A–Z/a–z。"));

            var appearance = NewTab("浮层");
            appearance.Children.Add(Heading("外观"));
            fontFamily = new ComboBox { ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(x => x).ToArray(), MinHeight = 32 };
            AddField(appearance, "字体", fontFamily);
            fontSize = new ComboBox { IsEditable = true, ItemsSource = new[] { "10", "12", "14", "16", "18", "20", "24", "32", "48" }, MinHeight = 32 };
            AddField(appearance, "字号", fontSize);
            background = Input();
            var colorRow = new Grid();
            colorRow.ColumnDefinitions.Add(new ColumnDefinition()); colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colorRow.Children.Add(background);
            var pick = Button("选色…"); pick.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(pick, 1);
            AutomationProperties.SetName(pick, "选择浮层背景色");
            pick.Click += delegate { PickColor(); }; colorRow.Children.Add(pick);
            AddField(appearance, "背景色", colorRow); AutomationProperties.SetName(background, "浮层背景色");
            appearance.Children.Add(Help("留空跟随系统明暗主题，或填写 #RRGGBB。文字颜色自动保持对比度。"));
            previewText = new TextBlock { Text = "译文会使用这个字体和背景。\nSelect text to translate.", TextWrapping = TextWrapping.Wrap };
            preview = new Border {
                Padding = new Thickness(15, 10, 15, 11), Margin = new Thickness(0, 12, 0, 20), CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1), MinHeight = 82, MaxHeight = 180,
                Child = new ScrollViewer { Content = previewText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            };
            AutomationProperties.SetName(preview, "浮层外观示例预览"); appearance.Children.Add(preview);
            appearance.Children.Add(Heading("停留时间"));
            autoHide = new ComboBox { IsEditable = true, ItemsSource = new[] { Automatic, "3", "6", "10", "15", "30", "60", "120" }, MinHeight = 32 };
            AddField(appearance, "自动消失（秒）", autoHide);
            appearance.Children.Add(Help("译文自动停留 6–22 秒，也可填 1–120 秒。悬停时续时，移远或继续操作仍会提前收起；等待和错误提示保留原时长。"));

            var launch = NewTab("启动");
            launch.Children.Add(Heading("登录 Windows 后启动"));
            autoStart = Toggle("开机自启（仅当前用户）"); launch.Children.Add(autoStart);
            startupStatus = Help(""); startupStatus.Margin = new Thickness(0, 0, 0, 14); launch.Children.Add(startupStatus);
            var refresh = Button("刷新启动状态"); refresh.HorizontalAlignment = HorizontalAlignment.Left;
            refresh.Click += delegate { RefreshStartup(); }; launch.Children.Add(refresh);
            launch.Children.Add(Help("只有修改此选项并保存时才创建或移除当前目录的启动快捷方式。不会提权，不使用服务或计划任务，也不会修改 Windows 的审批或防护设置。"));
            launch.Children.Add(Help("若存在指向其他副本的同名启动项，或 Windows 已禁用它，会提示原因并保留原状态。移动便携目录后，请检查原启动项。"));

            AutomationProperties.SetHelpText(fontSize, "范围 10 到 48，默认 14，随系统缩放");
            AutomationProperties.SetHelpText(background, "留空跟随系统主题，或输入不透明六位十六进制颜色 #RRGGBB");
            AutomationProperties.SetHelpText(autoHide, "自动，或 1 到 120 秒；鼠标悬停保留浮层");
            AutomationProperties.SetHelpText(englishPercent, "范围 0 到 100，分母为全部 Unicode 字母，不含空白、数字和标点");

            var deepL = NewTab("DeepL");
            deepL.Children.Add(Heading("DeepL 官方 API（可选）"));
            deepL.Children.Add(Help("选择“仅 DeepL”并保存密钥后，选中文字将发送给 DeepL，可能按账户套餐计费。不会自动回退到其他服务，也不会在自动模式中调用 DeepL。"));
            deepLStatus = Help(""); deepL.Children.Add(deepLStatus);
            deepLKey = new PasswordBox { MinHeight = 32, MaxLength = 1024, VerticalContentAlignment = VerticalAlignment.Center };
            AddField(deepL, "API 密钥", deepLKey);
            deepL.Children.Add(Help("留空保留已保存密钥；密钥不会回填。保存时使用 Windows 当前用户加密，单独存放，不写入普通配置。其他用户或电脑需重新输入。"));
            var clearKey = Button("清除已保存密钥"); clearKey.HorizontalAlignment = HorizontalAlignment.Left;
            clearKey.Click += delegate { StageDeepLClear(); }; deepL.Children.Add(clearKey);
            deepL.Children.Add(Help("新增、替换和清除都在点击“保存”后生效；取消不改变密钥。保存不会发起联网验证。"));
            deepL.Children.Add(Help("按官方密钥标记选择 api.deepl.com 或 api-free.deepl.com，不支持自定义地址。可用额度和费用请以 DeepL 账户为准。"));
            deepLKey.PasswordChanged += delegate { if (deepLKey.Password.Length != 0) clearDeepLKey = false; RefreshDeepLStatus(); };
            RefreshDeepLStatus();

            var footer = new StackPanel { Margin = new Thickness(24, 12, 24, 18) };
            error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(error, "设置错误"); footer.Children.Add(error);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var reset = Button("恢复配置默认值"); reset.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetHelpText(reset, "只恢复配置输入，不改变开机自启选择；保存后才生效");
            reset.Click += delegate { Fill(new Settings()); }; actions.Children.Add(reset);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = Button("取消"); cancel.Click += delegate { Close(); }; right.Children.Add(cancel);
            var save = Button("保存"); save.IsDefault = true; save.Margin = new Thickness(8, 0, 0, 0);
            save.Background = new SolidColorBrush(Color.FromRgb(56, 112, 210)); save.Foreground = Brushes.White;
            save.Click += delegate { Apply(Path.Combine(Program.Folder, "settings.json")); }; right.Children.Add(save);
            Grid.SetColumn(right, 1); actions.Children.Add(right); footer.Children.Add(actions);
            Grid.SetRow(footer, 2); layout.Children.Add(footer);
            provider.SelectionChanged += delegate { language.IsEnabled = provider.SelectedIndex != 2; indexModel.IsEnabled = provider.SelectedIndex == 4; };
            fontFamily.SelectionChanged += delegate { UpdatePreview(); };
            fontSize.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(delegate { UpdatePreview(); }));
            background.TextChanged += delegate { UpdatePreview(); };
            autoStart.Checked += delegate { StartupChoiceChanged(); }; autoStart.Unchecked += delegate { StartupChoiceChanged(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            settings.Changed += SyncEnabled;
            Closed += delegate { settings.Changed -= SyncEnabled; deepLKey.Clear(); };
            Activated += delegate { RefreshStartup(); };
            Fill(settings); RefreshStartup();
        }
        private StackPanel NewTab(string name)
        {
            var form = new StackPanel { Margin = new Thickness(16, 16, 16, 20) };
            tabs.Items.Add(new TabItem { Header = name, Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
            return form;
        }
        private static Brush Ink() { return new SolidColorBrush(Color.FromRgb(36, 40, 48)); }
        private static TextBlock Help(string text)
        { return new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(83, 91, 105)), FontSize = 12, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 6) }; }
        private static TextBlock Heading(string text)
        { return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Ink(), Margin = new Thickness(0, 0, 0, 10) }; }
        private static Button Button(string text)
        { return new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), MinHeight = 32, MinWidth = 64 }; }
        private static TextBox Input() { return new TextBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center }; }
        private static CheckBox Toggle(string text)
        { return new CheckBox { Content = text, VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 30, Margin = new Thickness(0, 0, 0, 4) }; }
        private static void AddField(Panel form, string label, FrameworkElement control)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Label { Content = label, Target = control, Padding = new Thickness(0, 5, 8, 0), Foreground = Ink() });
            AutomationProperties.SetName(control, label); Grid.SetColumn(control, 1); row.Children.Add(control); form.Children.Add(row);
        }
        private static string LanguageLabel(string code)
        { return Languages.FirstOrDefault(x => x.EndsWith(" · " + code, StringComparison.Ordinal)) ?? code ?? ""; }
        private static string AppsText(string[] apps) { return apps == null ? "" : string.Join("\n", apps); }
        internal void Fill(Settings value)
        {
            if (error != null) error.Visibility = Visibility.Collapsed;
            enabled.IsChecked = value.Enabled; clipboard.IsChecked = value.ClipboardFallback;
            fillingOverwrite = true;
            try { allowOverwrite.IsChecked = value.AllowClipboardOverwrite; }
            finally { fillingOverwrite = false; }
            provider.SelectedIndex = value.TranslationProvider == "microsoft" ? 1 : value.TranslationProvider == "tencent" ? 2 : value.TranslationProvider == "deepl" ? 3 : value.TranslationProvider == "index" ? 4 : 0;
            indexModel.SelectedItem = value.IndexModel;
            indexModel.IsEnabled = provider.SelectedIndex == 4;
            deepLKey.Clear(); clearDeepLKey = false; RefreshDeepLStatus();
            language.Text = LanguageLabel(value.Target);
            maxSelection.Text = value.MaxSelectionLength.ToString(CultureInfo.CurrentCulture);
            excludedApps.Text = AppsText(value.ExcludedApps);
            fontFamily.SelectedItem = BubbleAppearance.ResolveFont(value.BubbleFontFamily).Source;
            fontSize.Text = value.BubbleFontSize.ToString(CultureInfo.CurrentCulture);
            background.Text = value.BubbleBackground;
            autoHide.Text = value.AutoHideSeconds == 0 ? Automatic : value.AutoHideSeconds.ToString(CultureInfo.CurrentCulture);
            englishPercent.Text = value.MinEnglishPercent.ToString(CultureInfo.CurrentCulture);
            UpdatePreview();
        }
        private void SyncEnabled() { enabled.IsChecked = settings.Enabled; }
        private void UpdatePreview()
        {
            if (preview == null) return;
            double size;
            if (!double.TryParse(fontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out size) || size < 10 || size > 48 || double.IsNaN(size)) size = 14;
            var color = BubbleAppearance.Background(background.Text);
            previewText.FontFamily = BubbleAppearance.ResolveFont(fontFamily.SelectedItem as string);
            previewText.FontSize = size; previewText.LineHeight = size * 23 / 14;
            previewText.Foreground = new SolidColorBrush(BubbleAppearance.Foreground(color));
            preview.Background = new SolidColorBrush(color); preview.BorderBrush = new SolidColorBrush(BubbleAppearance.Border(color));
        }
        private void PickColor()
        {
            using (var dialog = new Forms.ColorDialog { FullOpen = true })
            {
                Color color;
                if (BubbleAppearance.TryParseBackground(background.Text, out color)) dialog.Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
                var owner = new Forms.NativeWindow(); owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                try {
                    if (dialog.ShowDialog(owner) == Forms.DialogResult.OK)
                        background.Text = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B);
                }
                finally { owner.ReleaseHandle(); }
            }
        }
        private void StartupChoiceChanged()
        {
            if (refreshingStartup) return;
            startupDirty = autoStart.IsChecked != observedStartup;
            RefreshStartup();
        }
        internal void RefreshStartup()
        {
            var state = startup.Read();
            refreshingStartup = true;
            try {
                autoStart.IsEnabled = state.CanChoose;
                if (!startupDirty) { observedStartup = state.Enabled; autoStart.IsThreeState = state.Enabled == null; autoStart.IsChecked = state.Enabled; }
                startupStatus.Text = state.Message + (startupDirty ? (autoStart.IsChecked == true ? "\n待保存：开启开机自启。" : "\n待保存：关闭开机自启。") : "");
            }
            finally { refreshingStartup = false; }
        }
        private void RefreshAfterFailure()
        { startupDirty = false; RefreshStartup(); }
        internal bool Apply(string path)
        {
            double size; int seconds, percent, length; Color color;
            if (!double.TryParse(fontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out size) || double.IsNaN(size) || double.IsInfinity(size) || size < 10 || size > 48)
                return Invalid("字号请输入 10–48 之间的数字。", fontSize, 1);
            if (!string.IsNullOrWhiteSpace(background.Text) && !BubbleAppearance.TryParseBackground(background.Text, out color))
                return Invalid("背景色请留空，或填写 #RRGGBB，例如 #25282F。", background, 1);
            if (autoHide.Text.Trim() == Automatic || autoHide.Text.Trim() == "0") seconds = 0;
            else if (!int.TryParse(autoHide.Text.Trim(), out seconds) || seconds < 1 || seconds > 120)
                return Invalid("自动消失时间请选择“自动”，或填写 1–120 秒。", autoHide, 1);
            if (!int.TryParse(englishPercent.Text.Trim(), out percent) || percent < 0 || percent > 100)
                return Invalid("最低英文占比请输入 0–100 的整数百分比。", englishPercent, 0);
            var next = settings.Clone();
            next.Enabled = enabled.IsChecked == true; next.ClipboardFallback = clipboard.IsChecked == true;
            next.AllowClipboardOverwrite = allowOverwrite.IsChecked == true;
            next.TranslationProvider = provider.SelectedIndex == 1 ? "microsoft" : provider.SelectedIndex == 2 ? "tencent" : provider.SelectedIndex == 3 ? "deepl" : provider.SelectedIndex == 4 ? "index" : "auto";
            if (provider.SelectedIndex == 4 && !IndexTranslator.ValidModel(indexModel.SelectedItem as string))
                return Invalid("请选择 Index 模型：2B、9B 或 35B。", indexModel, 0);
            next.IndexModel = indexModel.SelectedItem as string ?? settings.IndexModel;
            if (provider.SelectedIndex != 2 && language.Text != LanguageLabel(settings.Target))
            {
                string choice = Languages.FirstOrDefault(x => x == language.Text);
                string code = choice == null ? language.Text.Trim() : choice.Substring(choice.LastIndexOf(" · ", StringComparison.Ordinal) + 3);
                if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8})*$"))
                    return Invalid("请选择目标语言，或输入语言代码，例如 zh-Hans、en、ja。", language, 0);
                next.Target = code;
            }
            if (provider.SelectedIndex == 4)
            {
                try { IndexTranslator.TargetName(next.Target); }
                catch (IndexException ex) { return Invalid(ex.Message, language, 0); }
            }
            if (maxSelection.Text != settings.MaxSelectionLength.ToString(CultureInfo.CurrentCulture))
            {
                if (!int.TryParse(maxSelection.Text.Trim(), out length) || length < 1)
                    return Invalid("选区字符数上限请输入正整数，例如 2000。", maxSelection, 0);
                next.MaxSelectionLength = length;
            }
            if (excludedApps.Text != AppsText(settings.ExcludedApps))
            {
                string[] apps = excludedApps.Text.Split(new[] { '\r', '\n', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length != 0).ToArray();
                if (apps.Any(x => x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileNameWithoutExtension(x).Length == 0))
                    return Invalid("排除应用请填写进程名称，不要包含路径或通配符。", excludedApps, 0);
                next.ExcludedApps = apps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            next.BubbleFontFamily = BubbleAppearance.ResolveFont(fontFamily.SelectedItem as string).Source;
            next.BubbleFontSize = size; next.BubbleBackground = background.Text.Trim().ToUpperInvariant();
            next.AutoHideSeconds = seconds; next.MinEnglishPercent = percent;
            string newKey = deepLKey.Password.Trim();
            if (newKey.Length != 0 && !DeepLCredentials.ValidKey(newKey))
                return Invalid("API 密钥不能包含空白、换行或非 ASCII 字符。", deepLKey, 3);
            Action credentialRollback = null;
            Action rollback = null; string startupError;
            if (startupDirty && !startup.TrySet(autoStart.IsChecked == true, out rollback, out startupError))
            {
                RefreshAfterFailure(); return Invalid(startupError, autoStart, 2);
            }
            try
            {
                if (clearDeepLKey || newKey.Length != 0) credentialRollback = credentials.Replace(clearDeepLKey ? null : newKey);
            }
            catch
            {
                string message = "无法保存 DeepL 密钥，请检查应用目录权限；设置未保存。";
                if (rollback != null) { try { rollback(); } catch { message += " 启动项恢复失败，请检查启动页。"; } }
                RefreshAfterFailure(); return Invalid(message, deepLKey, 3);
            }
            if (!next.TrySave(path))
            {
                string message = "无法保存设置。请检查应用目录是否可写；当前配置未改变。";
                if (credentialRollback != null)
                {
                    try { credentialRollback(); }
                    catch { message += " DeepL 密钥恢复失败，请检查 DeepL 页并重新保存或清除。"; }
                }
                if (rollback != null)
                {
                    try { rollback(); }
                    catch { message += " 启动项恢复失败，请检查“启动”页显示的实际状态。"; }
                }
                RefreshAfterFailure(); return Invalid(message, null, rollback == null ? tabs.SelectedIndex : 2);
            }
            settings.CopyUserFrom(next);
            if (saved != null) saved();
            Close(); return true;
        }
        private bool Invalid(string message, Control control, int tab)
        {
            tabs.SelectedIndex = tab; error.Text = message; error.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(this, message);
            if (IsVisible && control != null) { control.BringIntoView(); control.Focus(); }
            return false;
        }
        internal void RenderPreview(string file, int tab = 1)
        {
            tabs.SelectedIndex = tab;
            layout.Measure(new Size(600, 740)); layout.Arrange(new Rect(0, 0, 600, 740)); layout.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 740, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
        internal void SetInputForTest(string size, string color, string seconds, string percent)
        { fontSize.Text = size; background.Text = color; autoHide.Text = seconds; englishPercent.Text = percent; }
        internal void SetBehaviorForTest(bool on, bool capture, string target, string maximum, string apps)
        { enabled.IsChecked = on; clipboard.IsChecked = capture; language.Text = target; maxSelection.Text = maximum; excludedApps.Text = apps; }
        private void RefreshDeepLStatus()
        {
            deepLStatus.Text = clearDeepLKey ? "待保存：清除密钥。" : deepLKey.Password.Length != 0 ? "待保存：替换密钥。" : credentials.Exists ? "已保存加密密钥（未验证）。" : "尚未配置密钥；DeepL 不会发起请求。";
        }
        private void StageDeepLClear() { deepLKey.Clear(); clearDeepLKey = true; RefreshDeepLStatus(); }
        internal void SetDeepLKeyForTest(string key) { deepLKey.Password = key; }
        internal void ClearDeepLKeyForTest() { StageDeepLClear(); }
        internal bool DeepLInputEmptyForTest { get { return deepLKey.Password.Length == 0; } }
        internal void SetProviderForTest(int value) { provider.SelectedIndex = value; }
        internal int ProviderForTest { get { return provider.SelectedIndex; } }
        internal void SetIndexModelForTest(string value) { indexModel.SelectedItem = value; }
        internal string IndexModelForTest { get { return indexModel.SelectedItem as string; } }
        internal bool IndexModelEnabledForTest { get { return indexModel.IsEnabled; } }
        internal bool TargetEnabledForTest { get { return language.IsEnabled; } }
        internal void SetOverwriteForTest(bool value) { allowOverwrite.IsChecked = value; }
        internal bool OverwriteForTest { get { return allowOverwrite.IsChecked == true; } }
        internal void SetStartupForTest(bool value) { autoStart.IsChecked = value; }
        internal bool EnabledForTest { get { return enabled.IsChecked == true; } }
        internal bool? StartupForTest { get { return autoStart.IsChecked; } }
        internal string ErrorForTest { get { return error.Text; } }
    }
}
