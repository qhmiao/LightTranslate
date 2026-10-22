using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightTranslate
{
    internal sealed class ClipboardOverwriteConfirmation : Window
    {
        private readonly Grid layout;
        internal ClipboardOverwriteConfirmation(Window owner = null)
        {
            if (owner != null) Owner = owner;
            Title = "允许覆盖原剪贴板？"; Width = 500; Height = 360;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14; Background = Brushes.White;
            Foreground = new SolidColorBrush(Color.FromRgb(36, 40, 48));
            layout = new Grid { Margin = new Thickness(24), Background = Brushes.White };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TextBlock { Text = "开启取词优先，允许覆盖", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
            var warning = new TextBlock { Text = "仅在无法完整备份时，直接复制选区并覆盖原剪贴板。\n\n" + ClipboardBackupException.Risk + "\n\n本选项将在设置页点击“保存”后生效。", TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(warning, 1); layout.Children.Add(warning);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "取消", IsDefault = true, IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 14, 8, 0) };
            cancel.Click += delegate { DialogResult = false; }; actions.Children.Add(cancel);
            var allow = new Button { Content = "了解风险，继续启用", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 14, 0, 0) };
            allow.Click += delegate { DialogResult = true; }; actions.Children.Add(allow);
            Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
        }
        internal void RenderPreview(string file)
        {
            layout.Measure(new Size(500, 321)); layout.Arrange(new Rect(0, 0, 500, 321)); layout.UpdateLayout();
            var bitmap = new RenderTargetBitmap(500, 321, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 500, 321));
            bitmap.Render(background);
            bitmap.Render(layout); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) png.Save(stream);
        }
    }
}
