using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SpokaSoundBoard.Services;

/// <summary>
/// Tells the app when an audio device is plugged, unplugged, enabled or disabled, so the device lists and the
/// engine follow without the « Actualiser » button. Windows calls back on its own thread.
/// </summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Action _changed;
    private bool _registered;

    public DeviceWatcher(Action changed)
    {
        _changed = changed;
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
    public void OnDeviceAdded(string pwstrDeviceId) => _changed();
    public void OnDeviceRemoved(string deviceId) => _changed();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { /* shutting down */ }
        }
        _enumerator.Dispose();
    }
}
