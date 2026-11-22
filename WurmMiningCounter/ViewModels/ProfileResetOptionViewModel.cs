using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class ProfileResetOptionViewModel : INotifyPropertyChanged
{
    private bool _isProfileResetEnabled = true;
    private bool _resetMaterialCounters;
    private bool _resetVeinContext;

    public ProfileResetOptionViewModel(
        PlayerProfileId profileId,
        string profileName,
        IEnumerable<MiningMaterialCountViewModel> materials,
        bool canResetVeinContext)
    {
        ProfileId = profileId;
        ProfileName = profileName ?? throw new ArgumentNullException(nameof(profileName));
        ArgumentNullException.ThrowIfNull(materials);
        MaterialOptions = new ReadOnlyCollection<MaterialResetOptionViewModel>(
            materials
                .Where(material => material.Count > 0)
                .Select(material => new MaterialResetOptionViewModel(material.Material, material.Count))
                .ToArray());
        foreach (var material in MaterialOptions)
            material.PropertyChanged += OnMaterialOptionPropertyChanged;

        CanResetVeinContext = canResetVeinContext;
    }

    public PlayerProfileId ProfileId { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ProfileName { get; }

    public IReadOnlyList<MaterialResetOptionViewModel> MaterialOptions { get; }

    public bool CanResetMaterialCounters => MaterialOptions.Count > 0;

    public bool CanResetVeinContext { get; }

    public bool IsMaterialCountersResetEnabled =>
        _isProfileResetEnabled && CanResetMaterialCounters;

    public bool IsVeinContextResetEnabled =>
        _isProfileResetEnabled && CanResetVeinContext;

    public bool IsSelectiveMaterialResetEnabled =>
        _isProfileResetEnabled && CanResetMaterialCounters && !ResetMaterialCounters;

    public bool HasSelectedMaterials => MaterialOptions.Any(material => material.IsSelected);

    public bool AreAllMaterialsSelected =>
        MaterialOptions.Count > 0 && MaterialOptions.All(material => material.IsSelected);

    public string SelectAllMaterialsText => AreAllMaterialsSelected ? "Clear All" : "Select All";

    public bool ResetMaterialCounters
    {
        get => _resetMaterialCounters;
        set
        {
            if (!IsMaterialCountersResetEnabled)
                value = false;

            if (!SetProperty(ref _resetMaterialCounters, value))
                return;

            if (value)
                ClearMaterialSelections();

            OnPropertyChanged(nameof(IsSelectiveMaterialResetEnabled));
        }
    }

    public bool ResetVeinContext
    {
        get => _resetVeinContext;
        set
        {
            if (!IsVeinContextResetEnabled)
                value = false;

            SetProperty(ref _resetVeinContext, value);
        }
    }

    public void SelectAllMaterials()
    {
        if (!IsSelectiveMaterialResetEnabled)
            return;

        if (AreAllMaterialsSelected)
        {
            ClearMaterialSelections();
            return;
        }

        foreach (var material in MaterialOptions)
            material.IsSelected = true;
    }

    public void ClearMaterialSelections()
    {
        foreach (var material in MaterialOptions)
            material.IsSelected = false;
    }

    public IReadOnlyList<MiningMaterial> GetSelectedMaterials() =>
        MaterialOptions
            .Where(material => material.IsSelected)
            .Select(material => material.Material)
            .ToArray();

    internal void SetProfileResetEnabled(bool enabled)
    {
        if (_isProfileResetEnabled == enabled)
            return;

        _isProfileResetEnabled = enabled;
        if (!enabled)
        {
            ResetMaterialCounters = false;
            ResetVeinContext = false;
            ClearMaterialSelections();
        }

        OnPropertyChanged(nameof(IsMaterialCountersResetEnabled));
        OnPropertyChanged(nameof(IsVeinContextResetEnabled));
        OnPropertyChanged(nameof(IsSelectiveMaterialResetEnabled));
    }

    private void OnMaterialOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MaterialResetOptionViewModel.IsSelected))
            return;

        if (sender is MaterialResetOptionViewModel { IsSelected: true } && ResetMaterialCounters)
            ResetMaterialCounters = false;

        OnPropertyChanged(nameof(HasSelectedMaterials));
        OnPropertyChanged(nameof(AreAllMaterialsSelected));
        OnPropertyChanged(nameof(SelectAllMaterialsText));
    }

    private bool SetProperty(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
