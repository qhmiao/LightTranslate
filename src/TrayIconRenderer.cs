using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class TrayIconRenderer
    {
        internal static int RunStandalone()
        {
            var results = new Dictionary<string, object>(); Test(results);
            File.WriteAllText(Path.Combine(Program.Folder, "tray-test.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(results));
            return 0;
        }
        internal static BitmapSource Render(Settings settings, int size)
        {
            var background = BubbleAppearance.Background(settings.BubbleBackground);
            var ink = new SolidColorBrush(BubbleAppearance.Foreground(background));
            var text = new FormattedText("译", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(BubbleAppearance.ResolveFont(settings.BubbleFontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                100, ink, 1.0);
            Geometry glyph = text.BuildGeometry(new Point());
            Rect bounds = glyph.Bounds;
            double scale = size * 0.64 / Math.Max(bounds.Width, bounds.Height);
            // Center the visible ink, not the font's line box or baseline.
            glyph.Transform = new MatrixTransform(scale, 0, 0, scale,
                size / 2.0 - (bounds.X + bounds.Width / 2) * scale,
                size / 2.0 - (bounds.Y + bounds.Height / 2) * scale);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawEllipse(new SolidColorBrush(background), null, new Point(size / 2.0, size / 2.0), size / 2.0 - 0.5, size / 2.0 - 0.5);
                drawing.DrawGeometry(ink, null, glyph);
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze();
            return bitmap;
        }

        internal static byte[] Encode(Settings settings)
        {
            int[] sizes = { 16, 20, 24, 32, 48, 64 };
            var frames = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (var stream = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render(settings, size)));
                    encoder.Save(stream); frames.Add(stream.ToArray());
                }
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]); writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var frame in frames) writer.Write(frame);
                return stream.ToArray();
            }
        }

        internal static System.Drawing.Icon Create(Settings settings)
        {
            using (var stream = new MemoryStream(Encode(settings)))
            using (var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize))
                return (System.Drawing.Icon)icon.Clone();
        }

        internal static void Update(Forms.NotifyIcon tray, Settings settings)
        {
            var replacement = Create(settings);
            var previous = tray.Icon;
            try { tray.Icon = replacement; }
            catch { replacement.Dispose(); throw; }
            if (previous != null) previous.Dispose();
        }

        internal static void Test(Dictionary<string, object> results)
        {
            string[] colors = { "#FFFFFF", "#25282F", "#3870D2" };
            string[] fonts = { "Microsoft YaHei UI", "SimSun", "Segoe UI" };
            var sheet = new DrawingVisual();
            using (var drawing = sheet.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, 720, 324));
                for (int row = 0; row < 3; row++)
                {
                    var settings = new Settings { BubbleBackground = colors[row], BubbleFontFamily = fonts[row] };
                    int x = 12;
                    foreach (int size in new[] { 16, 20, 24, 32, 48, 64 })
                    {
                        var bitmap = Render(settings, size);
                        drawing.DrawImage(bitmap, new Rect(x, row * 108 + 10, size, size));
                        x += 112;
                    }
                }
            }
            var preview = new RenderTargetBitmap(720, 324, 96, 96, PixelFormats.Pbgra32); preview.Render(sheet);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var file = File.Create(Path.Combine(Program.Folder, "tray-icon-preview.png"))) encoder.Save(file);
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            using (var tray = new Forms.NotifyIcon()) // Invisible: never adds a test icon to the user's tray.
            {
                Update(tray, new Settings());
                uint before = GetGuiResources(process.Handle, 1);
                for (int i = 0; i < 100; i++) Update(tray, new Settings { BubbleBackground = colors[i % colors.Length], BubbleFontFamily = fonts[i % fonts.Length] });
                uint after = GetGuiResources(process.Handle, 1);
                if (after > before + 2) throw new Exception("Tray icon handle leak");
                string path = Path.Combine(Program.Folder, "self-test-icon-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var settings = new Settings(); int updates = 0;
                    var editor = new SettingsWindow(settings, delegate { Update(tray, settings); updates++; }, new FakeStartupRegistration());
                    editor.SetInputForTest("14", "#3870D2", "0", "0");
                    if (!editor.Apply(path) || updates != 1 || settings.BubbleBackground != "#3870D2")
                        throw new Exception("Tray appearance save failed");
                    var reopened = new SettingsWindow(Settings.Load(path), delegate { updates++; }, new FakeStartupRegistration());
                    reopened.SetInputForTest("14", "#FFFFFF", "0", "0"); reopened.Close();
                    if (updates != 1 || Settings.Load(path).BubbleBackground != "#3870D2")
                        throw new Exception("Canceled appearance changed tray settings");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
                tray.Icon.Dispose(); tray.Icon = null;
            }
            results["trayIcon"] = "six sizes, three fonts/colors rendered; save/reopen/cancel passed; 100 replacements without USER handle growth";
        }

        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
