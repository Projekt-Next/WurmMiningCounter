using System.ComponentModel;
using System.Runtime.CompilerServices;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class EventAlertEditorViewModel : INotifyPropertyChanged
{
    private readonly EventAlertTrigger? _original;
    private string _name;
    private string _matchText;
    private string? _soundFileName;
    private string _popupText;
    private bool _enabled;
    private bool _soundEnabled;
    private bool _popupEnabled;

    public EventAlertEditorViewModel(EventAlertTrigger? trigger = null)
    {
        _original = trigger;
        _name = trigger?.Name ?? string.Empty;
        _matchText = trigger?.MatchText ?? string.Empty;
        _soundFileName = trigger?.SoundFileName;
        _popupText = trigger?.PopupText ?? string.Empty;
        _enabled = trigger?.Enabled ?? true;
        _soundEnabled = trigger?.SoundEnabled ?? true;
        _popupEnabled = trigger?.PopupEnabled ?? false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBuiltIn => _original?.IsBuiltIn == true;
    public bool CanEditDetails => !IsBuiltIn;
    public Guid Id => _original?.Id ?? Guid.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string MatchText { get => _matchText; set => Set(ref _matchText, value); }
    public string? SoundFileName { get => _soundFileName; set => Set(ref _soundFileName, value); }
    public string PopupText { get => _popupText; set => Set(ref _popupText, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool SoundEnabled { get => _soundEnabled; set => Set(ref _soundEnabled, value); }
    public bool PopupEnabled { get => _popupEnabled; set => Set(ref _popupEnabled, value); }

    public EventAlertTrigger Build() => IsBuiltIn
        ? _original! with
        {
            SoundFileName = SoundFileName,
            Enabled = Enabled,
            SoundEnabled = SoundEnabled,
            PopupEnabled = PopupEnabled
        }
        : new EventAlertTrigger(Id == Guid.Empty ? Guid.NewGuid() : Id, Name, MatchText, Enabled, SoundFileName, PopupText, IsBuiltIn: false, SoundEnabled, PopupEnabled);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
