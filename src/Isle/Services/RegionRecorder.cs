using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using WinRT;
using Isle.Native;

namespace Isle.Services
{
    /// <summary>
    /// Запись выбранной области экрана в MP4 своими силами Windows, без OBS и сторонних программ:
    /// кадры — Windows Graphics Capture (на видеокарте), вырезка области — копией текстуры на видеокарте,
    /// кодирование — H.264 через Media Foundation (аппаратный кодировщик, если есть). Звук компьютера и
    /// микрофон пишутся рядом в WAV и при остановке сводятся с видео в один файл.
    /// </summary>
    public sealed class RegionRecorder
    {
        public static RegionRecorder Current { get; private set; }
        /// <summary>Начало, конец, ошибка записи — для острова (точка REC и таймер).</summary>
        public static event Action StateChanged;

        public DateTime StartedAt { get; private set; }
        public bool Saving { get; private set; }
        public TimeSpan Elapsed => Saving ? _final : DateTime.Now - StartedAt;

        private readonly IntPtr _monitor;
        private readonly int _x, _y, _w, _h; // область в пикселях относительно монитора, размеры чётные
        private readonly bool _sysAudio;
        private bool _mic;
        private volatile bool _micMuted;
        /// <summary>Микрофон пишется сейчас (включён и не выключен на ходу).</summary>
        public bool MicOn => _micCap != null && !_micMuted;
        private const int Fps = 30;

        private ID3D11Device _d3d;
        private ID3D11DeviceContext _ctx;
        private IDirect3DDevice _device;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private MediaStreamSource _mss;
        private Task _transcode;
        private string _videoTmp;
        private readonly BlockingCollection<(IDirect3DSurface s, ID3D11Texture2D t, TimeSpan ts)> _frames = new(new ConcurrentQueue<(IDirect3DSurface, ID3D11Texture2D, TimeSpan)>(), 6);
        private TimeSpan? _t0;
        private TimeSpan _lastTs = TimeSpan.MinValue;
        private ID3D11Texture2D _lastTex;
        private volatile bool _stopping;
        private readonly object _lock = new();
        private TimeSpan _final;

        // звук
        private WasapiCapture _sysCap, _micCap;
        private WaveFileWriter _sysWav, _micWav;
        private string _sysPath, _micPath;
        private Stopwatch _audioClock;
        private long _audioStartQpc;

        private RegionRecorder(IntPtr monitor, int x, int y, int w, int h, bool sysAudio, bool mic)
        {
            _monitor = monitor; _x = x; _y = y; _w = Math.Max(16, w & ~1); _h = Math.Max(16, h & ~1);
            _sysAudio = sysAudio; _mic = mic;
        }

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Isle");

        /// <summary>Начать запись области (координаты — пиксели экрана). Ошибка — исключение с понятным текстом.</summary>
        public static async Task StartAsync(int screenX, int screenY, int w, int h, bool sysAudio, bool mic)
        {
            if (Current != null) throw new InvalidOperationException("запись уже идёт");
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("эта версия Windows не умеет записывать экран (нужна Windows 10 1903 или новее)");
            var center = new Win32.POINT { X = screenX + w / 2, Y = screenY + h / 2 };
            var mon = MonitorFromPoint(center, 2);
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            Win32.GetMonitorInfo(mon, ref mi);
            // область — в пределах монитора
            int x = Math.Max(0, screenX - mi.rcMonitor.Left), y = Math.Max(0, screenY - mi.rcMonitor.Top);
            int mw = mi.rcMonitor.Right - mi.rcMonitor.Left, mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            w = Math.Min(w, mw - x); h = Math.Min(h, mh - y);
            var r = new RegionRecorder(mon, x, y, w, h, sysAudio, mic);
            Current = r;
            try { await r.StartCoreAsync(); }
            catch { Current = null; r.Cleanup(); throw; }
            Journal.Info("record", "запись области " + r._w + "×" + r._h + " (" + Fps + " к/с)" + (sysAudio ? ", звук ПК" : "") + (mic ? ", микрофон" : ""));
            StateChanged?.Invoke();
        }

        private async Task StartCoreAsync()
        {
            // видеокарта: устройство D3D11 с поддержкой видео, защищённое от одновременного доступа (кадры и кодировщик — разные потоки)
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, flags, levels, out _d3d).Failure)
                D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out _d3d).CheckError();
            _ctx = _d3d.ImmediateContext;
            using (var mt = _d3d.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
            using (var dxgi = _d3d.QueryInterface<IDXGIDevice>())
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
                _device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                Marshal.Release(inspectable);
            }

            _item = CreateItemForMonitor(_monitor);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = true; } catch { }
            TryDisableBorder(_session); // Windows 11: без жёлтой рамки вокруг экрана

            // кодировщик: MP4 H.264, битрейт по размеру области
            var desc = new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_w, (uint)_h));
            _mss = new MediaStreamSource(desc) { BufferTime = TimeSpan.Zero };
            _mss.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            _mss.SampleRequested += OnSampleRequested;
            Directory.CreateDirectory(Folder);
            _videoTmp = Path.Combine(Path.GetTempPath(), "isle-rec-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
            File.WriteAllBytes(_videoTmp, Array.Empty<byte>());
            var file = await StorageFile.GetFileFromPathAsync(_videoTmp);
            var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_mss, stream, VideoProfile(withAudio: false));
            if (!prep.CanTranscode) throw new InvalidOperationException("кодировщик видео недоступен (" + prep.FailureReason + ")");
            _transcode = prep.TranscodeAsync().AsTask().ContinueWith(t => { stream.Dispose(); if (t.Exception != null) throw t.Exception.InnerException ?? t.Exception; });

            // звук — рядом, в WAV; тишина дописывается нулями, чтобы звук не «съезжал» относительно видео
            _audioClock = Stopwatch.StartNew();
            _audioStartQpc = Stopwatch.GetTimestamp();
            if (_sysAudio)
            {
                try { (_sysCap, _sysWav, _sysPath) = StartAudio(new WasapiLoopbackCapture(), "sys"); }
                catch (Exception ex) { Journal.Warn("record", "звук компьютера не включился: " + ex.Message); }
            }
            if (_mic)
            {
                try { (_micCap, _micWav, _micPath) = StartAudio(new WasapiCapture(), "mic"); }
                catch (Exception ex) { Journal.Warn("record", "микрофон не включился: " + ex.Message); }
            }

            StartedAt = DateTime.Now;
            _session.StartCapture();
        }

        private MediaEncodingProfile VideoProfile(bool withAudio)
        {
            var p = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            p.Video.Width = (uint)_w; p.Video.Height = (uint)_h;
            p.Video.FrameRate.Numerator = Fps; p.Video.FrameRate.Denominator = 1;
            p.Video.Bitrate = (uint)Math.Clamp(_w * (double)_h * Fps * 0.14, 2_000_000, 24_000_000);
            if (!withAudio) p.Audio = null;
            else { p.Audio = AudioEncodingProperties.CreateAac(48000, 2, 192000); }
            return p;
        }

        private (WasapiCapture, WaveFileWriter, string) StartAudio(WasapiCapture cap, string tag)
        {
            var path = Path.Combine(Path.GetTempPath(), "isle-rec-" + tag + "-" + Guid.NewGuid().ToString("N")[..8] + ".wav");
            var wav = new WaveFileWriter(path, cap.WaveFormat);
            var fmt = cap.WaveFormat;
            bool isMic = tag == "mic";
            cap.DataAvailable += (_, e) =>
            {
                lock (_lock)
                {
                    if (_stopping) return;
                    // микрофон выключили на ходу — вместо голоса тишина той же длины (дорожка не сдвигается)
                    if (isMic && _micMuted) Array.Clear(e.Buffer, 0, e.BytesRecorded);
                    // пока ничего не звучит, звуковая карта не присылает данных — заполняем паузу тишиной по часам
                    long expected = (long)(_audioClock.Elapsed.TotalSeconds * fmt.AverageBytesPerSecond);
                    expected -= expected % fmt.BlockAlign;
                    long gap = expected - (wav.Length + e.BytesRecorded);
                    if (gap > fmt.AverageBytesPerSecond / 10) WriteSilence(wav, gap - gap % fmt.BlockAlign);
                    wav.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };
            cap.StartRecording();
            return (cap, wav, path);
        }

        private static void WriteSilence(WaveFileWriter wav, long bytes)
        {
            var zero = new byte[Math.Min(bytes, 65536)];
            while (bytes > 0) { int n = (int)Math.Min(bytes, zero.Length); wav.Write(zero, 0, n); bytes -= n; }
        }

        /// <summary>
        /// Включить/выключить микрофон во время записи. Не был включён при старте — начинает писаться сейчас
        /// (начало дорожки заполняется тишиной по часам записи, звук не съезжает).
        /// </summary>
        public void SetMic(bool on)
        {
            if (_stopping) return;
            if (on && _micCap == null)
            {
                try { lock (_lock) (_micCap, _micWav, _micPath) = StartAudio(new WasapiCapture(), "mic"); _mic = true; }
                catch (Exception ex) { Journal.Warn("record", "микрофон не включился: " + ex.Message); return; }
            }
            _micMuted = !on;
            Journal.Info("record", "микрофон " + (on ? "включён" : "выключен") + " во время записи");
            StateChanged?.Invoke();
        }

        // ---------- кадры ----------
        private void OnFrame(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame == null || _stopping) return;
                var ts = frame.SystemRelativeTime;
                _t0 ??= ts;
                var rel = ts - _t0.Value;
                // не чаще заданной частоты кадров: монитор 144 Гц не должен раздувать файл и нагрузку
                if (_lastTs != TimeSpan.MinValue && rel - _lastTs < TimeSpan.FromSeconds(0.9 / Fps)) return;
                _lastTs = rel;
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                var iid = typeof(ID3D11Texture2D).GUID;
                var srcPtr = access.GetInterface(ref iid);
                using var src = new ID3D11Texture2D(srcPtr);
                var tex = Copy(src);
                Enqueue(tex, rel);
            }
            catch (Exception ex) { Journal.Debug("record", "кадр: " + ex.Message); }
        }

        private ID3D11Texture2D Copy(ID3D11Texture2D src)
        {
            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_w, (uint)_h, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget);
            var tex = _d3d.CreateTexture2D(desc);
            var sd = src.Description;
            int right = Math.Min(_x + _w, (int)sd.Width), bottom = Math.Min(_y + _h, (int)sd.Height);
            _ctx.CopySubresourceRegion(tex, 0, 0, 0, 0, src, 0, new Vortice.Mathematics.Box(_x, _y, 0, right, bottom, 1));
            // последний кадр храним: если экран замер, при остановке им «дотягиваем» видео до конца
            var keep = _d3d.CreateTexture2D(desc);
            _ctx.CopyResource(keep, tex);
            var old = Interlocked.Exchange(ref _lastTex, keep);
            old?.Dispose();
            return tex;
        }

        private void Enqueue(ID3D11Texture2D tex, TimeSpan ts)
        {
            using var dxgiSurface = tex.QueryInterface<IDXGISurface>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var p));
            var surface = MarshalInterface<IDirect3DSurface>.FromAbi(p);
            Marshal.Release(p);
            if (!_frames.TryAdd((surface, tex, ts), 200)) { surface.Dispose(); tex.Dispose(); } // кодировщик не успевает — кадр пропускаем
        }

        private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            try
            {
                // ждём следующий кадр; запись остановлена и очередь пуста — конец потока (Sample = null)
                if (!_frames.TryTake(out var f, Timeout.Infinite)) { args.Request.Sample = null; return; }
                var sample = MediaStreamSample.CreateFromDirect3D11Surface(f.s, f.ts);
                var (s, t) = (f.s, f.t);
                sample.Processed += (_, __) => { s.Dispose(); t.Dispose(); };
                args.Request.Sample = sample;
            }
            catch (InvalidOperationException) { args.Request.Sample = null; }
        }

        // ---------- остановка ----------
        /// <summary>Остановить запись и собрать файл. Возвращает путь к MP4.</summary>
        public static async Task<string> StopAsync()
        {
            var r = Current;
            if (r == null || r._stopping) return null;
            r._stopping = true;
            r.Saving = true;
            r._final = DateTime.Now - r.StartedAt;
            StateChanged?.Invoke();
            try { return await r.StopCoreAsync(); }
            finally
            {
                r.Cleanup();
                Current = null;
                StateChanged?.Invoke();
            }
        }

        private async Task<string> StopCoreAsync()
        {
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            // экран мог стоять без изменений — последний кадр дублируем на момент остановки, чтобы видео не было короче звука
            var end = _t0.HasValue ? TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - 0) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency))) - _t0.Value : TimeSpan.Zero;
            if (_lastTex != null && end > _lastTs + TimeSpan.FromSeconds(0.2))
            {
                try
                {
                    var desc = _lastTex.Description;
                    var tex = _d3d.CreateTexture2D(desc);
                    _ctx.CopyResource(tex, _lastTex);
                    Enqueue(tex, end);
                }
                catch { }
            }
            _frames.CompleteAdding();
            lock (_lock)
            {
                // звук: дотянуть тишиной до конца и закрыть
                foreach (var (cap, wav) in new[] { (_sysCap, _sysWav), (_micCap, _micWav) })
                {
                    if (cap == null) continue;
                    try { cap.StopRecording(); } catch { }
                    var fmt = cap.WaveFormat;
                    long expected = (long)(_audioClock.Elapsed.TotalSeconds * fmt.AverageBytesPerSecond); expected -= expected % fmt.BlockAlign;
                    if (expected > wav.Length) WriteSilence(wav, expected - wav.Length);
                    wav.Dispose();
                }
            }
            if (_transcode != null) await _transcode;
            if (!_t0.HasValue) throw new InvalidOperationException("ни одного кадра — Windows не дала записать экран");

            var name = "Запись " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".mp4";
            var final = Path.Combine(Folder, name);
            var tracks = new[] { _sysPath, _micPath };
            bool anyAudio = tracks[0] != null || tracks[1] != null;
            if (!anyAudio) { File.Move(_videoTmp, final); return final; }

            // сводим видео и звук в один файл; звук начали писать чуть раньше первого кадра — эту долю отрезаем
            var sw = Stopwatch.StartNew();
            var audioStart = TimeSpan.FromTicks((long)(_audioStartQpc * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));
            var lead = _t0.Value - audioStart;
            if (!await MuxAsync(_videoTmp, tracks, lead, final, VideoProfile(withAudio: true)))
            {
                // не свелось — сохраняем хотя бы видео без звука
                if (File.Exists(final)) File.Delete(final);
                File.Move(_videoTmp, final);
            }
            Journal.Info("record", "сведение видео и звука: " + sw.ElapsedMilliseconds + " мс");
            return final;
        }

        /// <summary>Видео + дорожки WAV → один MP4 (видео и звук AAC). false — не получилось (причина в журнале).</summary>
        public static async Task<bool> MuxAsync(string video, string[] wavs, TimeSpan lead, string output, MediaEncodingProfile profile)
        {
            try
            {
                var comp = new MediaComposition();
                comp.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(video)));
                foreach (var p in wavs)
                {
                    if (p == null || !File.Exists(p)) continue;
                    var track = await BackgroundAudioTrack.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(p));
                    if (lead > TimeSpan.Zero && lead < track.OriginalDuration) track.TrimTimeFromStart = lead;
                    comp.BackgroundAudioTracks.Add(track);
                }
                File.WriteAllBytes(output, Array.Empty<byte>());
                var outFile = await StorageFile.GetFileFromPathAsync(output);
                var result = await comp.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
                if (result != TranscodeFailureReason.None) { Journal.Warn("record", "звук не свёлся: " + result); return false; }
                return true;
            }
            catch (Exception ex) { Journal.Warn("record", "звук не свёлся: " + ex.Message); return false; }
        }

        /// <summary>Профиль MP4 под размер (для сведения и проверок).</summary>
        public static MediaEncodingProfile Profile(int w, int h, bool withAudio) => new RegionRecorder(IntPtr.Zero, 0, 0, w, h, false, false).VideoProfile(withAudio);

        private void Cleanup()
        {
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            foreach (var f in _frames) { try { f.s.Dispose(); f.t.Dispose(); } catch { } }
            try { _lastTex?.Dispose(); } catch { }
            try { _sysCap?.Dispose(); _micCap?.Dispose(); } catch { }
            try { _sysWav?.Dispose(); _micWav?.Dispose(); } catch { }
            foreach (var p in new[] { _sysPath, _micPath, _videoTmp })
                try { if (p != null && File.Exists(p)) File.Delete(p); } catch { }
            try { _ctx?.Dispose(); _d3d?.Dispose(); } catch { }
        }

        // ---------- WinRT ↔ D3D ----------
        [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
        [DllImport("d3d11.dll", ExactSpelling = true)] private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Win32.POINT pt, uint flags);

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
            IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
        }

        /// <summary>
        /// IGraphicsCaptureSession3.IsBorderRequired = false (Windows 11) — свойства нет в проекции SDK 19041,
        /// поэтому зовём напрямую по таблице методов. На Windows 10 интерфейса нет — рамка остаётся, это не ошибка.
        /// </summary>
        private static void TryDisableBorder(GraphicsCaptureSession session)
        {
            try
            {
                var unk = ((IWinRTObject)session).NativeObject.ThisPtr;
                var iid = new Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
                if (Marshal.QueryInterface(unk, ref iid, out var s3) != 0 || s3 == IntPtr.Zero) return;
                try
                {
                    var vtbl = Marshal.ReadIntPtr(s3);
                    var put = Marshal.GetDelegateForFunctionPointer<PutBool>(Marshal.ReadIntPtr(vtbl, 7 * IntPtr.Size)); // IInspectable(6) + get(1) → put
                    put(s3, 0);
                }
                finally { Marshal.Release(s3); }
            }
            catch { }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PutBool(IntPtr self, byte value);

        private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
        {
            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
            var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            var ptr = interop.CreateForMonitor(monitor, ref iid);
            try { return GraphicsCaptureItem.FromAbi(ptr); }
            finally { Marshal.Release(ptr); }
        }
    }
}
