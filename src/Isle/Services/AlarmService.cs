using System;
using System.IO;
using System.Media;
using System.Windows.Threading;

namespace Isle.Services
{
    /// <summary>
    /// Будильник для таймера и напоминаний: чёлка мигает красным и пищит, пока не нажмёшь на неё
    /// (или минуту, если пользователя нет за компьютером).
    /// </summary>
    public static class AlarmService
    {
        private static SoundPlayer _player;
        private static readonly DispatcherTimer AutoStop = new() { Interval = TimeSpan.FromSeconds(60) };
        public static bool Ringing { get; private set; }
        public static event Action Stopped;

        static AlarmService() { AutoStop.Tick += (_, __) => Stop(); }

        /// <param name="repeat">Для таймера: его длительность — в уведомлении появятся «Ещё раз» и ✓.</param>
        public static void Ring(string title, string subtitle, TimeSpan? repeat = null)
        {
            Ringing = true;
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Alarm, Title = title,
                // с кнопками подсказка «нажми» не нужна — и так видно, что жать
                Subtitle = repeat.HasValue ? subtitle ?? "" : (string.IsNullOrEmpty(subtitle) ? "" : subtitle + " · ") + "нажми, чтобы выключить",
                Icon = Icons.Bell, Accent = System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71),
                Duration = TimeSpan.FromSeconds(60), Important = true, ReplaceKey = "alarm", RepeatTimer = repeat
            });
            try
            {
                _player?.Stop();
                _player = new SoundPlayer(new MemoryStream(BeepWav()));
                _player.PlayLooping();
            }
            catch { try { SystemSounds.Exclamation.Play(); } catch { } }
            AutoStop.Stop(); AutoStop.Start();
        }

        public static void Stop()
        {
            if (!Ringing) return;
            Ringing = false;
            AutoStop.Stop();
            try { _player?.Stop(); } catch { }
            Stopped?.Invoke();
        }

        /// <summary>Мягкий двухтональный сигнал «пи-пи … пи-пи» (WAV в памяти, без файлов).</summary>
        private static byte[] BeepWav()
        {
            const int rate = 22050;
            var samples = new System.Collections.Generic.List<short>();
            void Tone(double hz, double sec, double vol)
            {
                int n = (int)(rate * sec);
                for (int i = 0; i < n; i++)
                {
                    double env = Math.Min(1, Math.Min(i / (rate * 0.01), (n - i) / (rate * 0.02))); // без щелчков
                    samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * vol * env * short.MaxValue));
                }
            }
            void Silence(double sec) { for (int i = 0; i < (int)(rate * sec); i++) samples.Add(0); }
            Tone(988, 0.12, 0.45); Silence(0.06); Tone(1319, 0.14, 0.45); Silence(0.14);
            Tone(988, 0.12, 0.45); Silence(0.06); Tone(1319, 0.14, 0.45); Silence(0.7);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int dataLen = samples.Count * 2;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(dataLen);
            foreach (var s in samples) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }
}
