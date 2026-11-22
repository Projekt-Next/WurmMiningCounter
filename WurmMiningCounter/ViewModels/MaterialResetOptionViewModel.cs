using System.ComponentModel;
using WurmMiningCounter.Models;

namespace WurmMiningCounter.ViewModels;

public sealed class MaterialResetOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public MaterialResetOptionViewModel(MiningMaterial material, long count)
    {
        Material = material;
        Count = count;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MiningMaterial Material { get; }

    public string DisplayName => MiningMaterialCountViewModel.GetDisplayName(Material);

    public long Count { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
