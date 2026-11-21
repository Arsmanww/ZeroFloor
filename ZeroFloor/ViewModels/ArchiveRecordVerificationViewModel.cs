using System.Windows.Input;
using ZeroFloor.Common;

namespace ZeroFloor.ViewModels;

public sealed class ArchiveRecordVerificationViewModel : ViewModelBase
{
    private sealed record Check(string Question, string SourceTitle, string Source,
        string ReferenceTitle, string Reference, string[] Answers, int CorrectIndex, string Hint);

    private static readonly Check[] Checks =
    [
        new(
            "Где искать копию дела Орлова?",
            "Кадровый реестр", "Техник Орлов. Личное дело Л-307 закрыто девять лет назад.",
            "Оглавление резервных пакетов", "09-А — Л-370\n09-Б — Л-307\n09-В — Л-317",
            ["Пакет 09-А", "Пакет 09-Б", "Пакет 09-В"], 1,
            "Сверь номер личного дела с описью пакетов."),
        new(
            "Какую часть дела стоит извлечь?",
            "Отметка отдела кадров", "Личный пропуск техника Орлова аннулирован.",
            "Опись пакета 09-Б", "Основная карточка — закрыта. Приложение о правопреемстве хранится отдельно и сохраняет силу после закрытия карточки.",
            ["Извлечь приложение о правопреемстве", "Восстановить старый пропуск", "Отбросить дело как недействительное"], 0,
            "Аннулирование пропуска касается основной карточки. Проверь, что сказано о приложении."),
        new(
            "Что отец оставил Максу?",
            "Закрытое приложение", "Основание: кровное родство. Кандидат: Максим Орлов. Идентификатор совпадает с профилем Макса.",
            "Примечание регистратора", "Запись даёт право запросить передачу должности. Полномочия выдаются только после отдельного подтверждения.",
            ["Действующий допуск Смотрителя", "Место в списке для уведомлений", "Право потребовать рассмотрения передачи должности"], 2,
            "В приложении указан кандидат. Проверь, выданы ли ему полномочия.")
    ];

    private readonly Action _onSuccess;
    private readonly Action _onFailure;
    private int _step;
    private int _selectedIndex = -1;
    private int _attemptsRemaining;
    private string _feedback = string.Empty;
    private bool _resolved;

    public ArchiveRecordVerificationViewModel(bool alert, Action onSuccess, Action onFailure)
    {
        _onSuccess = onSuccess;
        _onFailure = onFailure;
        _attemptsRemaining = alert ? 2 : 3;
        ArchiveStatus = alert ? "Тревога в архиве" : "Резервная копия";
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
    }

    private Check Current => Checks[_step];
    public string ArchiveStatus { get; }
    public string Stage => $"Сверка {_step + 1} из {Checks.Length}";
    public string Question => Current.Question;
    public string SourceTitle => Current.SourceTitle;
    public string Source => Current.Source;
    public string ReferenceTitle => Current.ReferenceTitle;
    public string Reference => Current.Reference;
    public IReadOnlyList<string> Answers => Current.Answers;
    public bool IsVerified { get; private set; }
    public string ConfirmLabel => IsVerified ? "Сохранить файл" : "Проверить вывод";
    public int AttemptsRemaining => _attemptsRemaining;
    public string Feedback => _feedback;
    public bool CanConfirm => !_resolved && (IsVerified || (_selectedIndex >= 0 && _selectedIndex < Answers.Count));
    public ICommand ConfirmCommand { get; }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (!SetProperty(ref _selectedIndex, value))
                return;

            RefreshConfirm();
        }
    }

    private void Confirm()
    {
        if (!CanConfirm)
            return;

        if (IsVerified)
        {
            _resolved = true;
            _onSuccess();
            return;
        }

        if (_selectedIndex != Current.CorrectIndex)
        {
            SetProperty(ref _attemptsRemaining, _attemptsRemaining - 1, nameof(AttemptsRemaining));
            if (_attemptsRemaining == 0)
            {
                _resolved = true;
                _onFailure();
                return;
            }

            SetProperty(ref _feedback, Current.Hint, nameof(Feedback));
            SelectedIndex = -1;
            return;
        }

        SelectedIndex = -1;
        SetProperty(ref _feedback, string.Empty, nameof(Feedback));
        if (_step == Checks.Length - 1)
        {
            IsVerified = true;
            OnPropertyChanged(nameof(IsVerified));
            OnPropertyChanged(nameof(ConfirmLabel));
            RefreshConfirm();
            return;
        }

        _step++;
        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(Question));
        OnPropertyChanged(nameof(SourceTitle));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(ReferenceTitle));
        OnPropertyChanged(nameof(Reference));
        OnPropertyChanged(nameof(Answers));
    }

    private void RefreshConfirm()
    {
        OnPropertyChanged(nameof(CanConfirm));
        CommandManager.InvalidateRequerySuggested();
    }
}
