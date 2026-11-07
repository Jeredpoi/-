using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Isle.Services
{
    public class AudioDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>Громкость, устройство вывода и уровень звука (для «живых» полосок).</summary>
    public class AudioService : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _enum = new();
        private MMDevice _device;
        private readonly Dispatcher _ui;
        private bool _suppress;

        public event Action<float, bool> VolumeChanged;
        public event Action DeviceChanged;

        public AudioService(Dispatcher ui)
        {
            _ui = ui;
            try { _enum.RegisterEndpointNotificationCallback(this); } catch { }
            Bind();
        }

        private void Bind()
        {
            try
            {
                if (_device != null)
                {
                    try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVol; } catch { }
                    _device.Dispose();
                }
                _device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _device.AudioEndpointVolume.OnVolumeNotification += OnVol;
            }
            catch { _device = null; }
        }

        private void OnVol(AudioVolumeNotificationData d)
        {
            if (_suppress) return;
            _ui.InvokeAsync(() => VolumeChanged?.Invoke(d.MasterVolume, d.Muted));
        }

        public float Volume
        {
            get { try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; } catch { return 0; } }
            set
            {
                try
                {
                    if (_device == null) return;
                    _suppress = true;
                    _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
                }
                catch { }
                finally { _suppress = false; }
            }
        }

        // ---------- Громкость отдельного приложения (как в «Микшере громкости» Windows) ----------

        /// <summary>
        /// Звуковые сессии плеера, который сейчас играет. AUMID у плееров разный: «Spotify.exe»,
        /// «SpotifyAB.SpotifyMusic_…!Spotify», «Chrome», «MSEdge», у Firefox — хэш. Сопоставляем с именем процесса.
        /// </summary>
        private List<AudioSessionControl> SessionsFor(string appId)
        {
            var result = new List<AudioSessionControl>();
            if (_device == null || string.IsNullOrWhiteSpace(appId)) return result;
            var id = appId.ToLowerInvariant();
            string want = id.Contains("spotify") ? "spotify"
                : id.Contains("msedge") ? "msedge"
                : id.Contains("chrome") ? "chrome"
                : id == "308046b0af4a39cb" || id.Contains("firefox") ? "firefox"
                : id.Contains("yandex") ? "yandex"
                : System.IO.Path.GetFileNameWithoutExtension(id.Split('!')[0].Split('_')[0].Split('\\').Last());
            try
            {
                var mgr = _device.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var ses = sessions[i];
                    if (ses.IsSystemSoundsSession) continue;
                    string name;
                    try { using var p = System.Diagnostics.Process.GetProcessById((int)ses.GetProcessID); name = p.ProcessName.ToLowerInvariant(); }
                    catch { continue; }
                    if (want.Length > 0 && (name.Contains(want) || want.Contains(name))) result.Add(ses);
                }
            }
            catch { }
            return result;
        }

        /// <summary>Громкость плеера 0..1 (как стоит в самом приложении / микшере Windows); null — плеер не найден.</summary>
        public float? AppVolume(string appId)
        {
            try
            {
                var list = SessionsFor(appId);
                if (list.Count == 0) return null;
                // у плеера бывает несколько сессий (старые, тихие) — берём ту, что сейчас звучит
                var playing = list.Where(x => x.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive).ToList();
                var pick = (playing.Count > 0 ? playing : list).OrderByDescending(x => { try { return x.AudioMeterInformation.MasterPeakValue; } catch { return 0f; } }).First();
                return pick.SimpleAudioVolume.Volume;
            }
            catch { return null; }
        }

        public void SetAppVolume(string appId, float v)
        {
            try
            {
                foreach (var ses in SessionsFor(appId)) ses.SimpleAudioVolume.Volume = Math.Clamp(v, 0f, 1f);
            }
            catch { }
        }

        public bool Muted
        {
            get { try { return _device?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
            set { try { if (_device != null) _device.AudioEndpointVolume.Mute = value; } catch { } }
        }

        /// <summary>Микрофон по умолчанию: выключен ли.</summary>
        public bool MicMuted
        {
            get
            {
                try { using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); return d.AudioEndpointVolume.Mute; }
                catch { return false; }
            }
            set
            {
                try { using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); d.AudioEndpointVolume.Mute = value; }
                catch { }
            }
        }

        public float Peak
        {
            get { try { return _device?.AudioMeterInformation.MasterPeakValue ?? 0; } catch { return 0; } }
        }

        public string DeviceName
        {
            get { try { return _device?.FriendlyName ?? "Нет устройства"; } catch { return ""; } }
        }

        public string DeviceId
        {
            get { try { return _device?.ID; } catch { return null; } }
        }

        public List<AudioDevice> OutputDevices()
        {
            var list = new List<AudioDevice>();
            try
            {
                foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    list.Add(new AudioDevice { Id = d.ID, Name = d.FriendlyName });
                    d.Dispose();
                }
            }
            catch { }
            return list;
        }

        public void SetDefault(string id)
        {
            try
            {
                var cfg = (IPolicyConfig)new PolicyConfigClient();
                cfg.SetDefaultEndpoint(id, ERole.eConsole);
                cfg.SetDefaultEndpoint(id, ERole.eMultimedia);
                cfg.SetDefaultEndpoint(id, ERole.eCommunications);
            }
            catch { }
        }

        // IMMNotificationClient
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            _ui.InvokeAsync(() => { Bind(); DeviceChanged?.Invoke(); });
        }

        public void Dispose()
        {
            try { _enum.UnregisterEndpointNotificationCallback(this); } catch { }
            _device?.Dispose();
            _enum.Dispose();
        }

        // Недокументированный, но давно стабильный интерфейс Windows для смены устройства по умолчанию.
        private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr fmt);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr fmt);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr a, IntPtr b);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
        }
    }
}
