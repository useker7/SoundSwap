using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SoundSwap;

/// <summary>
/// Windows ses cihazı değişikliklerini (varsayılan çıkış değişti, cihaz takıldı/çıkarıldı) dinler
/// ve olayları oluşturulduğu (arayüz) iş parçacığında yayınlar.
/// </summary>
sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    readonly MMDeviceEnumerator _enumerator;
    readonly SynchronizationContext _context;

    public event Action? DefaultOutputChanged;
    public event Action? DevicesChanged;

    public DeviceWatcher(MMDeviceEnumerator enumerator)
    {
        _enumerator = enumerator;
        _context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Must be created on the UI thread");
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    void Post(Func<Action?> handler) => _context.Post(_ => handler()?.Invoke(), null);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) Post(() => DefaultOutputChanged);
    }

    public void OnDeviceAdded(string pwstrDeviceId) => Post(() => DevicesChanged);
    public void OnDeviceRemoved(string deviceId) => Post(() => DevicesChanged);
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Post(() => DevicesChanged);
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose() => _enumerator.UnregisterEndpointNotificationCallback(this);
}
