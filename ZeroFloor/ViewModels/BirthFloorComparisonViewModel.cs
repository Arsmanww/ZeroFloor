using System.Windows.Input;
using ZeroFloor.Common;

namespace ZeroFloor.ViewModels;

public sealed class BirthFloorComparisonViewModel : ViewModelBase
{
    private readonly Action _onCompleted;
    private bool _isMiddleFloor;
    private bool _hasCompared;
    private bool _completed;

    public BirthFloorComparisonViewModel(Action onCompleted)
    {
        _onCompleted = onCompleted;
        SelectLowerFloorCommand = new RelayCommand(() => SelectFloor(false));
        SelectMiddleFloorCommand = new RelayCommand(() => SelectFloor(true));
        ContinueCommand = new RelayCommand(Complete, () => HasCompared && !_completed);
    }

    public bool IsMiddleFloor => _isMiddleFloor;
    public bool HasCompared => _hasCompared;
    public ICommand SelectLowerFloorCommand { get; }
    public ICommand SelectMiddleFloorCommand { get; }
    public ICommand ContinueCommand { get; }

    private void SelectFloor(bool middleFloor)
    {
        if (_completed || !SetProperty(ref _isMiddleFloor, middleFloor, nameof(IsMiddleFloor)))
            return;

        if (middleFloor)
            SetProperty(ref _hasCompared, true, nameof(HasCompared));

        CommandManager.InvalidateRequerySuggested();
    }

    private void Complete()
    {
        if (!HasCompared || _completed)
            return;

        _completed = true;
        _onCompleted();
    }
}
