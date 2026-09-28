using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;

namespace mTiles.ViewModels;

/// <summary>
/// The phone-dictation settings, on the Speech tab.
/// </summary>
/// <remarks>
/// A section on that tab rather than a tab of its own: it is the same feature reached from a different
/// microphone, and a user looking for it will look under dictation. Saved as you type, like the rest of
/// the tab. "Keep connected" starts or stops the bridge's connection to the relay.
/// </remarks>
public partial class SettingsViewModel
{
    [ObservableProperty] private bool _phoneEnabled;
    [ObservableProperty] private bool _phoneAutoSubmit;

    private void InitializePhone()
    {
        var phone = _settingsService.Settings.Phone;

        // Fields, for the reason InitializeSpeech gives: this runs from the constructor, and the setters
        // save.
#pragma warning disable MVVMTK0034
        _phoneEnabled = phone.Enabled;
        _phoneAutoSubmit = phone.AutoSubmitEnter;
#pragma warning restore MVVMTK0034
    }

    partial void OnPhoneEnabledChanged(bool value) => SavePhone(p => p.Enabled = value);

    partial void OnPhoneAutoSubmitChanged(bool value) => SavePhone(p => p.AutoSubmitEnter = value);

    private void SavePhone(Action<PhoneSettings> change)
    {
        change(_settingsService.Settings.Phone);
        _settingsService.NotifyChanged();
    }
}
