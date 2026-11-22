using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;
using WurmMiningCounter.ViewModels;

namespace WurmMiningCounter.Views;

public partial class AlertsWindow : Window, INotifyPropertyChanged
{
    private readonly EventAlertConfigurationController _controller;
    private readonly EventAlertSoundCatalog _soundCatalog;
    private readonly Func<string, bool> _deleteConfirmation;
    private bool _refreshing;
    private string? _errorMessage;
    private EventAlertEditorViewModel? _editor;
    private Guid? _editingId;

    public AlertsWindow(
        EventAlertConfigurationController controller,
        EventAlertSoundCatalog? soundCatalog = null,
        Func<string, bool>? deleteConfirmation = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _soundCatalog = soundCatalog ?? new EventAlertSoundCatalog();
        _deleteConfirmation = deleteConfirmation ?? (name => EventAlertDeleteConfirmation.Confirm(this, name));
        InitializeComponent();
        DataContext = this;
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AlertTriggerRow> AlertTriggers { get; } = [];
    public ObservableCollection<string> Sounds { get; } = [];
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public EventAlertEditorViewModel? Editor { get => _editor; private set => Set(ref _editor, value); }
    public bool IsEditorOpen => Editor is not null;
    public Visibility EditorVisibility => IsEditorOpen ? Visibility.Visible : Visibility.Collapsed;
    public string EditorTitle => _editingId is null ? "Add trigger" : "Edit trigger";

    private void Reload()
    {
        _refreshing = true;
        AlertTriggers.Clear();
        foreach (var trigger in _controller.GetSnapshot()) AlertTriggers.Add(new AlertTriggerRow(trigger, trigger.Id == _editingId));
        _refreshing = false;
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e) =>
        ApplyToggle(sender, (Guid id, bool value, out string? error) => _controller.TrySetEnabled(id, value, out error));

    private void OnSoundEnabledChanged(object sender, RoutedEventArgs e) =>
        ApplyToggle(sender, (Guid id, bool value, out string? error) => _controller.TrySetSoundEnabled(id, value, out error));

    private void OnPopupEnabledChanged(object sender, RoutedEventArgs e) =>
        ApplyToggle(sender, (Guid id, bool value, out string? error) => _controller.TrySetPopupEnabled(id, value, out error));

    private void ApplyToggle(object sender, ToggleChange change)
    {
        if (_refreshing || sender is not CheckBox { IsLoaded: true, Tag: AlertTriggerRow row } checkBox) return;
        if (!change(row.Trigger.Id, checkBox.IsChecked == true, out var error))
        {
            ErrorMessage = error;
            Reload();
        }
        else { ErrorMessage = null; Reload(); }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        Reload();
        RefreshSounds();
        Editor = new EventAlertEditorViewModel();
        ErrorMessage = null;
        RaiseEditorProperties();
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AlertTriggerRow row }) return;
        _editingId = row.Trigger.Id;
        RefreshSounds(row.Trigger.SoundFileName);
        Editor = new EventAlertEditorViewModel(row.Trigger);
        ErrorMessage = null;
        Reload();
        RaiseEditorProperties();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseEditor();

    private void OnSaveEditorClick(object sender, RoutedEventArgs e)
    {
        if (Editor is null) return;
        var success = _editingId is null
            ? _controller.TryAddCustom(Editor.Build(), out var error)
            : _controller.TryEdit(Editor.Build(), out error);
        if (!success)
        {
            ErrorMessage = error;
            return;
        }

        ErrorMessage = null;
        CloseEditor();
        Reload();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AlertTriggerRow { Trigger: { IsBuiltIn: false } } row }) return;
        if (!_deleteConfirmation(row.Name)) return;
        if (!_controller.TryDeleteCustom(row.Trigger.Id, out var error)) ErrorMessage = error;
        else
        {
            ErrorMessage = null;
            if (_editingId == row.Trigger.Id)
                CloseEditor();
            else
                Reload();
        }
    }

    private void CloseEditor()
    {
        _editingId = null;
        Editor = null;
        Reload();
        RaiseEditorProperties();
    }

    private void RefreshSounds(string? selectedSoundFileName = null)
    {
        var sounds = _soundCatalog.GetSnapshot().ToList();
        if (!string.IsNullOrWhiteSpace(selectedSoundFileName) && !sounds.Contains(selectedSoundFileName, StringComparer.Ordinal))
            sounds.Add(selectedSoundFileName);

        Sounds.Clear();
        foreach (var sound in sounds.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal))
            Sounds.Add(sound);
    }

    private void RaiseEditorProperties()
    {
        OnPropertyChanged(nameof(IsEditorOpen));
        OnPropertyChanged(nameof(EditorVisibility));
        OnPropertyChanged(nameof(EditorTitle));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private delegate bool ToggleChange(Guid id, bool value, out string? error);
}

public sealed class AlertTriggerRow(EventAlertTrigger trigger, bool isEditing)
{
    public EventAlertTrigger Trigger { get; } = trigger;
    public bool Enabled => Trigger.Enabled;
    public bool SoundEnabled => Trigger.SoundEnabled;
    public bool PopupEnabled => Trigger.PopupEnabled;
    public bool AreTogglesEnabled => !isEditing;
    public string Name => Trigger.Name;
    public string MatchText => Trigger.MatchText;
    public string? SoundFileName => Trigger.SoundFileName;
    public string PopupText => Trigger.PopupText;
    public bool CanEdit => true;
    public Visibility DeleteVisibility => Trigger.IsBuiltIn ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LockVisibility => Trigger.IsBuiltIn ? Visibility.Visible : Visibility.Collapsed;
}
