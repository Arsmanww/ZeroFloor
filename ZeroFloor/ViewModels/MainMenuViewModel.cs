using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ZeroFloor.Common;
using ZeroFloor.Services;

namespace ZeroFloor.ViewModels;

public sealed class MainMenuViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public MainMenuViewModel(AppServices services)
    {
        _services = services;

        NewGameCommand = new AsyncRelayCommand(StartNewGameAsync);
        ContinueGameCommand = new AsyncRelayCommand(ContinueGameAsync, () => _services.SaveService.HasAnySave);
        LoadGameCommand = new RelayCommand(_ => OpenLoadScreen());
        SettingsCommand = new RelayCommand(_ => OpenSettings());
        ExitCommand = new RelayCommand(_ => ConfirmExit());
    }

    /// <summary>Читается View (MainMenuView.xaml.cs) для подключения отдельного фонового
    /// изображения главного меню — тот же принцип доступа, что и NovelViewModel.Assets.</summary>
    public IAssetService Assets => _services.Asset;

    public ICommand NewGameCommand { get; }
    public ICommand ContinueGameCommand { get; }
    public ICommand LoadGameCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ExitCommand { get; }

    private async Task StartNewGameAsync()
    {
        _services.ScreenTransition.ShowLoading("Загрузка новой игры…");
        await Task.Delay(600);
        _services.Navigation.NavigateTo(new NovelViewModel(_services));
        _services.ScreenTransition.HideLoading();
    }

    private async Task ContinueGameAsync()
    {
        var mostRecent = _services.SaveService.FindMostRecentSave();
        if (mostRecent is null)
            return;

        _services.ScreenTransition.ShowLoading("Загрузка сохранения…");
        await Task.Delay(600);

        try
        {
            var data = mostRecent.Value.Kind == SaveSlotKind.Auto
                ? _services.SaveService.LoadAutoSave()
                : _services.SaveService.LoadManualSlot(mostRecent.Value.SlotIndex);

            _services.Navigation.NavigateTo(new NovelViewModel(_services, data));
        }
        catch (SaveLoadException ex)
        {
            _services.Notification.Publish($"Не удалось загрузить сохранение: {ex.Message}");
        }

        _services.ScreenTransition.HideLoading();
    }

    private void OpenLoadScreen() =>
        _services.Navigation.NavigateTo(new SaveLoadViewModel(
            _services,
            SaveLoadMode.Load,
            currentSnapshotProvider: null,
            onSlotLoaded: data => _services.Navigation.NavigateTo(new NovelViewModel(_services, data)),
            onBack: OnReturnToMenu));

    private void OpenSettings() =>
        _services.Navigation.NavigateTo(new SettingsViewModel(_services, OnReturnToMenu));

    private void OnReturnToMenu() =>
        _services.Navigation.NavigateTo(new MainMenuViewModel(_services));

    private void ConfirmExit() =>
        _services.Confirmation.RequestConfirmation(new ConfirmationRequest(
            "Выйти из игры?",
            "Все несохранённые данные текущей сессии будут потеряны.",
            OnConfirm: () => Application.Current.Shutdown(),
            IsDanger: true));
}
