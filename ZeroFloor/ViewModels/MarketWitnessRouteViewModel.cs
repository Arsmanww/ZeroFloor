using System.Windows.Input;
using ZeroFloor.Common;

namespace ZeroFloor.ViewModels;

public sealed class MarketWitnessRouteViewModel : ViewModelBase
{
    private readonly Action _onCompleted;
    private bool _traderMarked;
    private bool _loaderMarked;
    private string? _selectedPoint;
    private string _instruction = "Нанесите оба показания на схему.";
    private bool _solved;
    private bool _completed;

    public MarketWitnessRouteViewModel(Action onCompleted)
    {
        _onCompleted = onCompleted;
        MarkTraderCommand = new RelayCommand(() => MarkWitness(true), () => !TraderMarked);
        MarkLoaderCommand = new RelayCommand(() => MarkWitness(false), () => !LoaderMarked);
        SelectPointCommand = new RelayCommand(point => SelectPoint((string?)point), _ => CanSelectPoint);
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
    }

    public bool TraderMarked => _traderMarked;
    public bool LoaderMarked => _loaderMarked;
    public string? SelectedPoint => _selectedPoint;
    public string Instruction => _instruction;
    public bool IsSolved => _solved;
    public bool CanSelectPoint => TraderMarked && LoaderMarked && !IsSolved;
    public bool CanConfirm => !_completed && (IsSolved || (CanSelectPoint && SelectedPoint is not null));
    public string ConfirmLabel => IsSolved ? "К складам" : "Подтвердить маршрут";
    public ICommand MarkTraderCommand { get; }
    public ICommand MarkLoaderCommand { get; }
    public ICommand SelectPointCommand { get; }
    public ICommand ConfirmCommand { get; }

    private void MarkWitness(bool trader)
    {
        if (trader)
            SetProperty(ref _traderMarked, true, nameof(TraderMarked));
        else
            SetProperty(ref _loaderMarked, true, nameof(LoaderMarked));

        if (CanSelectPoint)
            SetProperty(ref _instruction, "Где сходятся оба следа? Отметьте точку на карте.", nameof(Instruction));
        RefreshCommands();
    }

    private void SelectPoint(string? point)
    {
        if (!CanSelectPoint || point is not ("pipe" or "passage" or "gate"))
            return;

        SetProperty(ref _selectedPoint, point, nameof(SelectedPoint));
        SetProperty(ref _instruction, "Сверьте выбранную точку с обоими показаниями.", nameof(Instruction));
        RefreshCommands();
    }

    private void Confirm()
    {
        if (!CanConfirm)
            return;

        if (IsSolved)
        {
            _completed = true;
            _onCompleted();
        }
        else if (SelectedPoint == "passage")
        {
            SetProperty(ref _solved, true, nameof(IsSolved));
            SetProperty(ref _instruction, "Оба маршрута сходятся у прохода к складам Гоши.", nameof(Instruction));
            OnPropertyChanged(nameof(ConfirmLabel));
        }
        else
        {
            SetProperty(ref _selectedPoint, null, nameof(SelectedPoint));
            SetProperty(ref _instruction, "Эту точку видел только один свидетель. Ищите пересечение следов.", nameof(Instruction));
        }
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanSelectPoint));
        OnPropertyChanged(nameof(CanConfirm));
        CommandManager.InvalidateRequerySuggested();
    }
}
