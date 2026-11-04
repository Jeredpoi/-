using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace Isle.Services
{
    /// <summary>
    /// Спектр звука, который сейчас играет в системе (WASAPI loopback + БПФ).
    /// Bands[0] — басы (слева), Bands[N-1] — высокие (справа). Значения 0..1, сглажены.
    /// Работает только пока играет музыка — в остальное время выключен и не тратит процессор.
    /// </summary>
    public class SpectrumService : IDisposable
    {
        public const int BandCount = 28;
        private const int FftSize = 2048; // 2^11
        private const int FftM = 11;

        private WasapiLoopbackCapture _cap;
        private readonly float[] _ring = new float[FftSize];
        private int _ringPos;
        private int _samplesSinceFft;
        private readonly Complex[] _fft = new Complex[FftSize];
        private readonly float[] _bands = new float[BandCount];
        private readonly float[] _norm = new float[BandCount];
        private int _sampleRate = 48000;
        private readonly object _lock = new();

        public bool Running => _cap != null;

        /// <summary>Копия текущих полос (безопасно читать из UI).</summary>
        public float[] Snapshot()
        {
            var copy = new float[BandCount];
            lock (_lock) Array.Copy(_bands, copy, BandCount);
            return copy;
        }

        public void Start()
        {
            if (_cap != null) return;
            try
            {
                _cap = new WasapiLoopbackCapture();
                _sampleRate = _cap.WaveFormat.SampleRate;
                _cap.DataAvailable += OnData;
                _cap.RecordingStopped += (_, __) => { };
                _cap.StartRecording();
            }
            catch { _cap = null; }
        }

        public void Stop()
        {
            var c = _cap;
            _cap = null;
            if (c == null) return;
            try { c.DataAvailable -= OnData; c.StopRecording(); c.Dispose(); } catch { }
            lock (_lock) Array.Clear(_bands, 0, BandCount);
        }

        private void OnData(object s, WaveInEventArgs e)
        {
            var f = (s as WasapiLoopbackCapture)?.WaveFormat;
            if (f == null) return;
            int ch = Math.Max(1, f.Channels);
            int bps = f.BitsPerSample / 8;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
            int frame = bps * ch;
            int frames = e.BytesRecorded / frame;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = i * frame + c * bps;
                    sum += isFloat ? BitConverter.ToSingle(e.Buffer, o) : bps == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f : 0;
                }
                _ring[_ringPos] = sum / ch;
                _ringPos = (_ringPos + 1) % FftSize;
                if (++_samplesSinceFft >= FftSize / 4) // ~4 раза за окно → плавно
                {
                    _samplesSinceFft = 0;
                    Analyze();
                }
            }
        }

        private void Analyze()
        {
            for (int i = 0; i < FftSize; i++)
            {
                float v = _ring[(_ringPos + i) % FftSize];
                _fft[i].X = (float)(v * FastFourierTransform.HannWindow(i, FftSize));
                _fft[i].Y = 0;
            }
            FastFourierTransform.FFT(true, FftM, _fft);

            // логарифмические полосы 40 Гц … 16 кГц
            double fMin = 40, fMax = Math.Min(16000, _sampleRate / 2.0);
            double binHz = _sampleRate / (double)FftSize;
            var fresh = new float[BandCount];
            for (int b = 0; b < BandCount; b++)
            {
                double lo = fMin * Math.Pow(fMax / fMin, b / (double)BandCount);
                double hi = fMin * Math.Pow(fMax / fMin, (b + 1) / (double)BandCount);
                int i0 = Math.Max(1, (int)(lo / binHz)), i1 = Math.Max(i0 + 1, (int)(hi / binHz));
                double acc = 0;
                for (int i = i0; i < i1 && i < FftSize / 2; i++)
                    acc = Math.Max(acc, Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
                // в децибелы и в 0..1; высоким частотам чуть помогаем — их энергия меньше
                double db = 20 * Math.Log10(acc + 1e-9) + b * 0.55;
                fresh[b] = (float)Math.Clamp((db + 62) / 52.0, 0, 1);
            }
            // Своя «громкость» для каждой полосы: запоминаем недавний максимум и делим на него.
            // Иначе высокие частоты всегда почти нулевые, а басы — всегда в потолок.
            for (int b = 0; b < BandCount; b++)
            {
                _norm[b] = Math.Max(fresh[b], _norm[b] * 0.996f);
                float floor = _norm[b] * 0.35f;                       // «тишина» для этой полосы
                float v = _norm[b] > 0.05f ? (fresh[b] - floor) / Math.Max(0.08f, _norm[b] - floor) : 0;
                fresh[b] = Math.Clamp(v, 0, 1);
            }
            lock (_lock)
            {
                for (int b = 0; b < BandCount; b++)
                {
                    // быстро вверх, медленно вниз
                    float cur = _bands[b];
                    _bands[b] = fresh[b] > cur ? cur + (fresh[b] - cur) * 0.7f : cur * 0.82f + fresh[b] * 0.18f;
                }
            }
        }

        public void Dispose() => Stop();
    }
}
