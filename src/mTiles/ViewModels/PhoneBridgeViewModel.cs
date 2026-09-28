using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Services.Phone;

namespace mTiles.ViewModels;

/// <summary>A phone that has paired.</summary>
internal sealed partial class PhoneDeviceViewModel(PhoneDevice device, Func<PhoneDevice, Task> unpair)
    : ObservableObject
{
    public string Label => device.Name;

    public bool IsConnected => device.IsConnected;

    public string Since => device.IsConnected
        ? "connected"
        : $"last seen {Ago(device.LastSeen)}";

    [RelayCommand]
    private Task Unpair() => unpair(device);

    private static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.Now - when;
        return elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} min ago"
            : elapsed.TotalDays < 1 ? when.ToLocalTime().ToString("HH:mm")
            : when.ToLocalTime().ToString("d MMM");
    }
}

/// <summary>
/// The panel that pairs a phone: one QR code, the phones already paired, and nothing about networks.
/// </summary>
/// <remarks>
/// <para>What used to fill this panel — two codes for two kinds of network, a certificate warning, a
/// firewall diagnosis and a list of alternative addresses — was all the cost of a port being opened on
/// this machine. The link goes through a relay now, so a phone anywhere scans one code and is paired.</para>
/// <para>Opening the panel connects to the relay and closing it lets go again, unless a phone is paired
/// or Settings asks to stay connected. The code on screen is withdrawn when the panel closes: it is a
/// secret whose lifetime is exactly the panel's.</para>
/// </remarks>
internal sealed partial class PhoneBridgeViewModel : ObservableObject, IDisposable
{
    private readonly PhoneBridgeManager _manager;
    private readonly DispatcherTimer _clock;
    private IDisposable? _hold;
    private PhoneInvitation? _invitation;
    private bool _disposed;

    public PhoneBridgeViewModel(PhoneBridgeManager manager)
    {
        _manager = manager;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
        _manager.StateChanged += OnManagerChanged;
    }

    /// <summary>Wired from the view, which knows which window's clipboard to use.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public ObservableCollection<PhoneDeviceViewModel> Devices { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Connecting to the relay…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    private Bitmap? _qrImage;

    public bool HasCode => QrImage is not null;

    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _expiresText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenewCommand))]
    private bool _canRenew;

    public bool HasDevices => Devices.Count > 0;

    /// <summary>Connects and puts a code on screen.</summary>
    public async Task InitializeAsync()
    {
        _hold ??= _manager.HoldOpen();
        IsBusy = true;
        Error = null;

        try
        {
            if (!await _manager.StartAsync())
            {
                Error = _manager.LastError ?? "mTiles could not reach the relay.";
                Status = "Not connected";
                CanRenew = true;
                return;
            }

            RefreshDevices();
            await ShowNewCodeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Said into the panel when starting failed in a way the start did not report itself.</summary>
    public void ReportStartupFailure(string message)
    {
        IsBusy = false;
        Error = message;
        Status = "Not connected";
        CanRenew = true;
    }

    [RelayCommand(CanExecute = nameof(CanRenew))]
    private async Task Renew()
    {
        if (!_manager.IsRunning)
        {
            await InitializeAsync();
            return;
        }

        await RevokeShownAsync();
        await ShowNewCodeAsync();
    }

    [RelayCommand]
    private Task CopyLink() => Url.Length > 0 && CopyToClipboard is { } copy ? copy(Url) : Task.CompletedTask;

    private async Task ShowNewCodeAsync()
    {
        CanRenew = false;
        try
        {
            var invitation = await _manager.InviteAsync();
            _invitation = invitation;
            Url = invitation.Url;
            Code = invitation.Code;
            QrImage = QrCodeImage.Render(invitation.Url, pixelsPerModule: 5);
            Error = null;
            Status = "Scan the code with your phone's camera";
            Tick();
            _clock.Start();
        }
        catch (PhoneBridgeException ex)
        {
            ClearCode();
            Error = ex.Message;
            Status = "No room for another device";
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("A phone invitation could not be made: {0}", ex);
            ClearCode();
            Error = "mTiles could not make a pairing code. Check the connection and try again.";
            Status = "Not connected";
        }
        finally
        {
            CanRenew = true;
        }
    }

    private void Tick()
    {
        if (_invitation is not { } invitation)
        {
            ExpiresText = "";
            return;
        }

        var left = invitation.ExpiresAt - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            // Taken off the screen rather than left there looking usable: a code that has expired pairs
            // nothing, and a phone that scans it is told so by the relay a while later, which reads as the
            // feature not working.
            ClearCode();
            Status = "The code expired";
            ExpiresText = "";
            return;
        }

        ExpiresText = $"valid for {(int)left.TotalMinutes}:{left.Seconds:00} · single use";
    }

    private void ClearCode()
    {
        _clock.Stop();
        _invitation = null;
        QrImage = null;
        Url = "";
        Code = "";
        ExpiresText = "";
    }

    private void OnManagerChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        var before = Devices.Count;
        RefreshDevices();

        // A new phone paired with the code on screen: it was single use, so what is left on screen is a
        // spent code. A fresh one is put up for the next phone rather than leaving one that does nothing.
        if (Devices.Count > before && _invitation is not null)
        {
            Status = "Paired";
            _ = Renew();
        }
    });

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var device in _manager.Devices)
            Devices.Add(new PhoneDeviceViewModel(device, UnpairAsync));
        OnPropertyChanged(nameof(HasDevices));
    }

    private async Task UnpairAsync(PhoneDevice device)
    {
        try
        {
            await _manager.UnpairAsync(device);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Unpairing a phone failed: {0}", ex);
            Error = "mTiles could not unpair that device.";
        }
        RefreshDevices();
    }

    private async Task RevokeShownAsync()
    {
        if (_invitation is { } shown)
        {
            _invitation = null;
            await _manager.RevokeAsync(shown.Id);
        }
    }

    /// <summary>Withdraws the code on screen and lets the link go unless something still needs it.</summary>
    public async Task CloseAsync()
    {
        _clock.Stop();
        await RevokeShownAsync();

        var hold = _hold;
        _hold = null;
        hold?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _clock.Stop();
        _manager.StateChanged -= OnManagerChanged;
    }
}
