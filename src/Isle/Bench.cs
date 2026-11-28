using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Isle.Helpers;
using Isle.Services;

namespace Isle
{
    public static class Bench
    {
        private static void Snap(FrameworkElement v, string dir, string name, double W, double H)
        {
            v.Measure(new Size(W, H)); v.Arrange(new Rect(0, 0, W, H)); v.UpdateLayout();
            const double k = 3;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(W * k), (int)(H * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 22)), null, new Rect(0, 0, W, H));
            rtb.Render(dv);
            rtb.Render(v); // напрямую, без VisualBrush — тот растягивал содержимое
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png")); enc.Save(fs);
        }

        private static LyricLine L(double at, double end, params (string w, double a, double e)[] ws)
        {
            var l = new LyricLine { At = TimeSpan.FromSeconds(at), End = TimeSpan.FromSeconds(end), Words = new List<LyricWord>() };
            var sb = new StringBuilder();
            foreach (var (w, a, e) in ws) { l.Words.Add(new LyricWord { At = TimeSpan.FromSeconds(a), End = TimeSpan.FromSeconds(e), Text = w }); sb.Append(w); }
            l.Text = sb.ToString().Trim();
            return l;
        }

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            var log = new StringBuilder();
            try
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.AutoUpdateCheck = false;
                await Task.Delay(800);
                var lines = new List<LyricLine>
                {
                    new LyricLine { At = TimeSpan.FromSeconds(0), Text = "" },
                    L(6, 9, ("I ", 6, 6.3), ("think ", 6.3, 6.8), ("I'm ", 6.8, 7.2), ("an ", 7.2, 7.5), ("impostor", 7.5, 9)),
                    L(9.2, 12, ("Every ", 9.2, 9.8), ("single ", 9.8, 10.4), ("day ", 10.4, 12)),
                    new LyricLine { At = TimeSpan.FromSeconds(12.5), Text = "" },
                    L(17, 20, ("Nobody ", 17, 17.8), ("knows ", 17.8, 18.4), ("it's ", 18.4, 18.8), ("me", 18.8, 20)),
                    new LyricLine { At = TimeSpan.FromSeconds(20.2), End = TimeSpan.FromSeconds(23), Text = "Строка без разметки по словам, синтетическая заливка" },
                    L(23.5, 26, ("Ooh ", 23.5, 24), ("yeah", 24, 26)),
                    L(26.2, 29, ("Last ", 26.2, 26.8), ("line ", 26.8, 27.4), ("here", 27.4, 29)),
                };
                lines[6].OppositeAligned = true;
                double[] times = { 3.0, 6.55, 6.95, 7.9, 8.6, 10.0, 11.3, 14.0, 16.5, 18.2, 21.5, 24.8, 27.0 };
                foreach (var t in times)
                {
                    var list = LyricsScroller.Still(lines, TimeSpan.FromSeconds(t), 182, 220);
                    Snap(list, dir, $"home-{t:00.00}", 182, 220);
                    var pill = PillLyric.Still(lines, TimeSpan.FromSeconds(t), "Impostor Syndrome — Sidney Gish", 300, 18);
                    Snap(pill, dir, $"pill-{t:00.00}", 300, 18);
                    log.AppendLine("ok " + t);
                }
                // анимация: 30 кадров по 1/30 с вокруг слова impostor
                var anim = LyricsScroller.Still(lines, TimeSpan.FromSeconds(7.4), 182, 220);
                for (int i = 0; i < 30; i++)
                {
                    var pos = TimeSpan.FromSeconds(7.4 + i / 30.0 * 1.2);
                    anim.Render(pos, 1.2 / 30);
                    Snap(anim, dir, $"anim-{i:00}", 182, 220);
                }
            }
            catch (Exception ex) { log.AppendLine("ОШИБКА " + ex); }
            File.WriteAllText(Path.Combine(dir, "karaoke.txt"), log.ToString(), Encoding.UTF8);
            Application.Current.Shutdown();
        }
    }
}
