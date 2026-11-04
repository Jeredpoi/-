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
        private readonly float[] _fresh = new float[BandCount];
        private readonly double[] _db = new double[BandCount];
        private double _peakDb = -60;

        // ---- темп: ищем удары по басам и считаем интервалы между ними
        private double _bassAvg, _clock;
        private double _lastBeat = -1;
        private readonly double[] _intervals = new double[16];
        private int _intervalCount, _intervalPos;
        private double _bpm, _energy;

        /// <summary>Темп музыки в ударах в минуту (0 — пока не понял).</summary>
        public double Bpm { get { lock (_lock) return _bpm; } }
        /// <summary>Насколько «плотная» музыка сейчас, 0..1 (доля громких полос).</summary>
        public double Energy { get { lock (_lock) return _energy; } }
        /// <summary>Сколько секунд назад был последний удар баса (для танца в такт).</summary>
        public double SinceBeat { get { lock (_lock) return _lastBeat < 0 ? 99 : _clock - _lastBeat; } }

        private void DetectBeat(bool silent)
        {
            double hop = (FftSize / 2.0) / _sampleRate;
            _clock += hop;
            // энергия басов (первые полосы) относительно своего среднего
            double bass = 0;
            for (int b = 0; b < 5; b++) bass += Math.Pow(10, (_db[b] - _peakDb) / 20);
            _bassAvg = _bassAvg * 0.96 + bass * 0.04;
            double energy = 0;
            for (int b = 0; b < BandCount; b++) energy += _fresh[b];
            energy /= BandCount;
            if (silent) { lock (_lock) { _energy *= 0.9; } return; }
            if (bass > _bassAvg * 1.45 && bass > 0.05 && (_lastBeat < 0 || _clock - _lastBeat > 0.28))
            {
                if (_lastBeat >= 0)
                {
                    double iv = _clock - _lastBeat;
                    if (iv > 0.28 && iv < 1.6) { _intervals[_intervalPos] = iv; _intervalPos = (_intervalPos + 1) % _intervals.Length; _intervalCount = Math.Min(_intervals.Length, _intervalCount + 1); }
                }
                lock (_lock) _lastBeat = _clock;
                if (_intervalCount >= 4)
                {
                    // медиана интервалов → удары в минуту, приводим к 70–180
                    var arr = new double[_intervalCount];
                    Array.Copy(_intervals, arr, _intervalCount);
                    Array.Sort(arr);
                    double bpm = 60 / arr[arr.Length / 2];
                    while (bpm < 70) bpm *= 2;
                    while (bpm > 180) bpm /= 2;
                    lock (_lock) _bpm = _bpm <= 0 ? bpm : _bpm * 0.8 + bpm * 0.2;
                }
            }
            lock (_lock) _energy = _energy * 0.9 + energy * 0.1;
        }

        /// <summary>Темп по списку интервалов между ударами (для тестов).</summary>
        public static double BpmFromIntervals(double[] intervals)
        {
            var arr = (double[])intervals.Clone();
            Array.Sort(arr);
            double bpm = 60 / arr[arr.Length / 2];
            while (bpm < 70) bpm *= 2;
            while (bpm > 180) bpm /= 2;
            return bpm;
        }
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
            Array.Clear(_norm, 0, BandCount);
            lock (_lock) { _bpm = 0; _energy = 0; _lastBeat = -1; }
            _intervalCount = 0; _peakDb = -60;
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
                    sum += isFloat ? BitConverter.ToSingle(e.Buffer, o)
                         : bps == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f
                         : bps == 3 ? (e.Buffer[o] | e.Buffer[o + 1] << 8 | (sbyte)e.Buffer[o + 2] << 16) / 8388608f
                         : bps == 4 ? BitConverter.ToInt32(e.Buffer, o) / 2147483648f : 0;
                }
                _ring[_ringPos] = sum / ch;
                _ringPos = (_ringPos + 1) % FftSize;
                if (++_samplesSinceFft >= FftSize / 2) // ~45 раз в секунду: чаще экран всё равно не обновляется
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
            var fresh = _fresh;
            double loudest = -200;
            for (int b = 0; b < BandCount; b++)
            {
                double lo = fMin * Math.Pow(fMax / fMin, b / (double)BandCount);
                double hi = fMin * Math.Pow(fMax / fMin, (b + 1) / (double)BandCount);
                int i0 = Math.Max(1, (int)(lo / binHz)), i1 = Math.Max(i0 + 1, (int)(hi / binHz));
                double acc = 0;
                for (int i = i0; i < i1 && i < FftSize / 2; i++)
                    acc = Math.Max(acc, Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
                // децибелы; высоким частотам чуть помогаем — их энергия меньше
                _db[b] = 20 * Math.Log10(acc + 1e-12) + b * 0.55;
                loudest = Math.Max(loudest, _db[b]);
            }

            // Автоусиление: шкала идёт от недавней самой громкой полосы, а не от фиксированного уровня.
            // Поэтому эквалайзер одинаково живой и на громкости 100%, и почти на нуле —
            // звук, который слышно, всегда даёт движение.
            _peakDb = Math.Max(loudest, _peakDb - 0.06);      // громкость «забывается» ~3 дБ/с — после убавления звука подстроится за несколько секунд
            const double Range = 42;                           // показываем верхние 42 дБ
            bool silent = loudest < -125;                      // настоящая цифровая тишина (пауза, конец трека)
            for (int b = 0; b < BandCount; b++)
            {
                float v = silent ? 0 : (float)Math.Clamp((_db[b] - (_peakDb - Range)) / Range, 0, 1);
                // своя «громкость» для каждой полосы, чтобы высокие не были всегда внизу, а басы — в потолке
                _norm[b] = Math.Max(0.3f, Math.Max(v, _norm[b] * 0.998f));
                float floor = _norm[b] * 0.35f;
                fresh[b] = Math.Clamp((v - floor) / Math.Max(0.12f, _norm[b] - floor), 0, 1);
            }
            DetectBeat(silent);
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
