using System;
using System.Collections.Generic;
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

        public bool Muted
        {
            get { try { return _device?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
            set { try { if (_device != null) _device.AudioEndpointVolume.Mute = value; } catch { } }
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
