using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace WurmMiningCounter.ViewModels;

public sealed class ResetSelectionViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private bool _resetSession;

    public ResetSelectionViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        ProfileOptions = new ReadOnlyCollection<ProfileResetOptionViewModel>(
            _mainViewModel.ProfilePanels
                .Select(panel => new ProfileResetOptionViewModel(
                    panel.ProfileId,
                    panel.ProfileName,
                    panel.Materials,
                    panel.HasVeinContext))
                .ToArray());

        foreach (var option in ProfileOptions)
            option.PropertyChanged += OnProfileOptionPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ProfileResetOptionViewModel> ProfileOptions { get; }

    public bool ResetSession
    {
        get => _resetSession;
        set
        {
            if (_resetSession == value)
                return;

            _resetSession = value;
            foreach (var option in ProfileOptions)
                option.SetProfileResetEnabled(!value);

            OnPropertyChanged();
            OnPropertyChanged(nameof(CanReset));
        }
    }

    public bool CanReset => ResetSession || ProfileOptions.Any(option =>
        option.ResetMaterialCounters || option.HasSelectedMaterials || option.ResetVeinContext);

    public string ConfirmationText
    {
        get
        {
            if (ResetSession)
            {
                return "You are about to reset the entire session.\n\nThis will reset:\n"
                    + "- Total counters\n- Material counters\n- Vein Context\n"
                    + "- current session state\n- all profiles\n\nAre you sure?";
            }

            var message = new StringBuilder("You are about to reset:");
            foreach (var option in ProfileOptions.Where(option =>
                         option.ResetMaterialCounters || option.HasSelectedMaterials || option.ResetVeinContext))
            {
                message.Append("\n\n").Append(option.ProfileName);
                if (option.ResetMaterialCounters)
                    message.Append("\n- Material counters");
                if (option.HasSelectedMaterials)
                {
                    message.Append("\nSelected material counters:");
                    foreach (var material in option.MaterialOptions.Where(material => material.IsSelected))
                        message.Append("\n").Append(material.DisplayName).Append(": ").Append(material.Count);
                }
                if (option.ResetVeinContext)
                    message.Append("\n- Vein Context");
            }

            message.Append("\n\nTotal and other counters will not be affected.\n\nAre you sure?");
            return message.ToString();
        }
    }

    public async Task<bool> ConfirmAndExecuteAsync(Func<string, bool> confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);

        if (!CanReset || !confirm(ConfirmationText))
            return false;

        await ExecuteAsync();
        return true;
    }

    public async Task ExecuteAsync()
    {
        if (!CanReset)
            return;

        if (ResetSession)
        {
            await _mainViewModel.ResetSessionAsync();
            return;
        }

        foreach (var option in ProfileOptions.Where(option => option.ResetMaterialCounters))
            _mainViewModel.ResetProfileMaterialCounters(option.ProfileId);

        foreach (var option in ProfileOptions.Where(option =>
                     !option.ResetMaterialCounters && option.HasSelectedMaterials))
        {
            _mainViewModel.ResetMaterials(option.ProfileId, option.GetSelectedMaterials());
        }

        foreach (var option in ProfileOptions.Where(option => option.ResetVeinContext))
            await _mainViewModel.ResetVeinContextAsync(option.ProfileId);
    }

    private void OnProfileOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileResetOptionViewModel.ResetMaterialCounters)
            or nameof(ProfileResetOptionViewModel.HasSelectedMaterials)
            or nameof(ProfileResetOptionViewModel.ResetVeinContext))
        {
            OnPropertyChanged(nameof(CanReset));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
