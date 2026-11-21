using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ZeroFloor.Common;
using ZeroFloor.Engine;
using ZeroFloor.Models;
using ZeroFloor.Services;

namespace ZeroFloor.ViewModels;

/// <summary>
/// Игровой экран новеллы — управляется движком (GameSessionController), а не встроенным
/// демо-скриптом. По умолчанию (новая игра) начинает реальный сюжет с Главы 1
/// (Data/Scenario/CH01_LOW_FLOW.json, загружается App.xaml.cs). Демонстрационный сценарий
/// Data/Demo/animation_showcase.json (явно демонстрационный, не реальный сюжет — см.
/// CLAUDE.md) больше не используется как точка входа — он остаётся только как фикстура для
/// ZeroFloor.Tests/AnimationShowcaseScenarioTests.cs.
/// При загрузке сохранения (<paramref name="savedData"/>) полностью восстанавливает
/// GameState (переменные, флаги, appliedChoiceIds, историю выборов, журнал реплик) и
/// возобновляет сессию ровно на сохранённом узле (requirement "проверь сохранение и
/// загрузку на разных этапах прохождения").
/// </summary>
public sealed class NovelViewModel : ViewModelBase
{
    public const string DefaultStartSceneId = "CH01_SC01";

    private const int MaxSkipSteps = 500;

    /// <summary>Русские подписи для служебных ролей, у которых намеренно нет записи в
    /// Data/Characters/characters.json (раздел 14a PROJECT_ANALYSIS.md — второстепенные
    /// персонажи без портрета): рассказчик и безликие голоса из «Каталога ассетов» полного
    /// сценария (npc_cellmate, npc_official, npc_neighbor и т.п.). Основной состав получает
    /// подпись из CharacterDefinition.DisplayName (ниже), а не отсюда.</summary>
    private static readonly IReadOnlyDictionary<string, string> MinorSpeakerDisplayNames = new Dictionary<string, string>
    {
        ["narrator"] = "Рассказчик",
        ["npc_cellmate"] = "Сокамерник",
        ["npc_official"] = "Представитель Вессель",
        ["npc_neighbor"] = "Сосед",
        ["npc_journalist"] = "Журналист",
        ["npc_witness"] = "Свидетель"
    };

    private readonly AppServices _services;
    private readonly GameSessionController _controller;
    private readonly Stopwatch _sessionStopwatch = Stopwatch.StartNew();
    private readonly int _baselinePlayTimeSeconds;

    private DispatcherTimer? _autoAdvanceTimer;

    private string _speakerName = string.Empty;
    private string _lineText = string.Empty;
    private bool _isChoiceVisible;
    private bool _isLogVisible;
    private bool _isUiHidden;
    private bool _isPauseMenuVisible;
    private int _selectedChoiceIndex;
    private WirePuzzleViewModel? _activeMinigame;
    private TimingBarViewModel? _activeTimingBarMinigame;
    private StealthGridViewModel? _activeStealthGrid;
    private CodeAssemblyViewModel? _activeCodeAssembly;
    private CableDiagnosticsViewModel? _activeCableDiagnostics;
    private RingLockViewModel? _activeRingLock;
    private LogScanViewModel? _activeLogScan;
    private Episode3DViewModel? _activeEpisode;
    private DossierAssemblyViewModel? _activeDossierAssembly;
    private BirthFloorComparisonViewModel? _activeBirthFloorComparison;
    private ArchiveRecordVerificationViewModel? _activeArchiveRecordVerification;
    private MarketWitnessRouteViewModel? _activeMarketWitnessRoute;
    private MarketCardGameViewModel? _activeMarketCardGame;

    // Индикатор подозрения (Глава 5, SuspicionMeterCue) — persistent HUD, не мини-игра: не
    // блокирует диалог/выбор, просто показывает текущее значение флага поверх экрана, пока не
    // встретится узел с Hide. См. RefreshFromCurrentNode.
    private bool _isSuspicionMeterVisible;
    private string _suspicionMeterFlagKey = "suspicion";
    private int _suspicionMeterMin;
    private int _suspicionMeterMax = 10;
    private int _suspicionMeterYellowThreshold;
    private int _suspicionMeterRedThreshold;

    // Таймер ответа на текущем узле с выбором (Глава 5, ChoiceTimerCue) — по истечении
    // автоматически применяет заранее заданный выбор, как обычный ConfirmChoice. Отдельно от
    // мини-игр выше: это не блокирующий оверлей, а ограничение по времени поверх обычного
    // ChoiceListControl. См. StartChoiceTimeoutIfNeeded/StopChoiceTimeout.
    private DispatcherTimer? _choiceTimeoutTimer;
    private string? _choiceTimeoutChoiceId;
    private double _choiceTimeoutTotalSeconds;
    private double _choiceTimeoutRemainingSeconds;

    public NovelViewModel(AppServices services, SaveSlotData? savedData = null)
    {
        _services = services;

        var sceneManager = new SceneManager(services.ScenarioRepository);
        var effectApplier = new EffectApplier();
        var dialogueManager = new DialogueManager(sceneManager, effectApplier);
        var conditionEvaluator = new ConditionEvaluator();
        var choiceProcessor = new ChoiceProcessor(conditionEvaluator, effectApplier);
        var endingManager = new EndingManager(conditionEvaluator);
        var state = savedData?.GameState ?? new GameState();

        _controller = new GameSessionController(dialogueManager, choiceProcessor, endingManager, state);
        _controller.AutoSaveRequested += OnAutoSaveRequested;

        _baselinePlayTimeSeconds = savedData?.PlayTimeSeconds ?? 0;

        Choices = new ObservableCollection<ChoiceOptionViewModel>();
        LogEntries = new ObservableCollection<DialogueLogEntry>(state.DialogueLog.Select(ToDisplayEntry));

        ContinueCommand = new RelayCommand(_ => Advance(), _ => !IsChoiceVisible && !IsAnyMinigameActive);
        ToggleLogCommand = new RelayCommand(_ => IsLogVisible = !IsLogVisible, _ => !IsAnyMinigameActive);
        ToggleUiCommand = new RelayCommand(_ => IsUiHidden = !IsUiHidden, _ => !IsChoiceVisible && !IsAnyMinigameActive);
        RestoreUiCommand = new RelayCommand(_ => IsUiHidden = false);
        OpenPauseMenuCommand = new RelayCommand(_ => IsPauseMenuVisible = true, _ => !IsAnyMinigameActive);
        SkipCommand = new RelayCommand(_ => Skip(), _ => !IsChoiceVisible && !_controller.HasEnded && !IsAnyMinigameActive);

        SelectPreviousChoiceCommand = new RelayCommand(_ => MoveChoiceSelection(-1), _ => IsChoiceVisible);
        SelectNextChoiceCommand = new RelayCommand(_ => MoveChoiceSelection(1), _ => IsChoiceVisible);
        ConfirmSelectedChoiceCommand = new RelayCommand(
            _ => Choices[_selectedChoiceIndex].ConfirmCommand.Execute(null),
            _ => IsChoiceVisible && Choices.Count > 0);
        ReplayVoiceCommand = new RelayCommand(_ => _services.Audio.ReplayVoice(), _ => _services.Audio.HasVoiceForCurrentLine);

        PauseMenu = new PauseMenuViewModel(
            onResume: () => IsPauseMenuVisible = false,
            onSave: OpenSaveScreen,
            onOpenLoad: OpenLoadScreen,
            onOpenSettings: OpenSettingsScreen,
            onExitToMenu: ConfirmExitToMenu,
            onExitApplication: ConfirmExitApplication);

        _services.Settings.Changed += OnSettingsChanged;

        if (savedData is { SceneId: not null, NodeId: not null })
            _controller.Resume(savedData.SceneId!, savedData.NodeId!);
        else
            _controller.Start(savedData?.SceneId ?? DefaultStartSceneId);

        RefreshFromCurrentNode(isSceneEntry: true);
    }

    public ObservableCollection<ChoiceOptionViewModel> Choices { get; }
    public ObservableCollection<DialogueLogEntry> LogEntries { get; }
    public PauseMenuViewModel PauseMenu { get; }

    /// <summary>Сигнал View: узел сменился, вот что нужно проанимировать (см. NodeAnimationCues).
    /// Чистые данные — сама анимация строится во View, не здесь (MVVM).</summary>
    public event EventHandler<NodeAnimationCues>? AnimationCuesReady;

    /// <summary>Последние переданные реплики-ремарки — на случай, если View подписался на
    /// AnimationCuesReady уже ПОСЛЕ того, как событие отработало (конструктор ViewModel
    /// вызывает его раньше, чем DataTemplate успевает создать View и подписать обработчик).
    /// View должен прочитать это сразу после подписки и, если не null, обработать как есть.</summary>
    public NodeAnimationCues? LastAnimationCues { get; private set; }

    public string SpeakerName
    {
        get => _speakerName;
        private set => SetProperty(ref _speakerName, value);
    }

    public string LineText
    {
        get => _lineText;
        private set => SetProperty(ref _lineText, value);
    }

    public bool IsChoiceVisible
    {
        get => _isChoiceVisible;
        private set
        {
            if (SetProperty(ref _isChoiceVisible, value))
                RestartAutoAdvanceTimerIfNeeded();
        }
    }

    public bool IsLogVisible
    {
        get => _isLogVisible;
        set
        {
            if (SetProperty(ref _isLogVisible, value))
                RestartAutoAdvanceTimerIfNeeded();
        }
    }

    public bool IsUiHidden
    {
        get => _isUiHidden;
        set => SetProperty(ref _isUiHidden, value);
    }

    public bool IsPauseMenuVisible
    {
        get => _isPauseMenuVisible;
        set
        {
            if (SetProperty(ref _isPauseMenuVisible, value))
                RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Мини-игра текущего узла (см. DialogueNode.MinigameCue) — null, если узел её не
    /// задаёт. Пока она не null, продолжение/пропуск/авточтение заблокированы (см. их
    /// CanExecute/RestartAutoAdvanceTimerIfNeeded ниже): игрок должен решить головоломку или
    /// нажать "повтор" внутри неё самой, прежде чем сюжет продолжится.</summary>
    public WirePuzzleViewModel? ActiveMinigame
    {
        get => _activeMinigame;
        private set
        {
            if (ReferenceEquals(_activeMinigame, value))
                return;

            _activeMinigame?.Dispose();
            _activeMinigame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Мини-игра «Кража обходчика» (Глава 2, TimingBar) — тот же принцип, что у
    /// ActiveMinigame выше, отдельный слот, потому что у неё другая ViewModel/разметка и, в
    /// отличие от WirePuzzle, провал ведёт не на повтор, а на другой узел сцены (см.
    /// MinigameCue.FailureNodeId, JumpToMinigameOutcomeNode ниже).</summary>
    public TimingBarViewModel? ActiveTimingBarMinigame
    {
        get => _activeTimingBarMinigame;
        private set
        {
            if (ReferenceEquals(_activeTimingBarMinigame, value))
                return;

            _activeTimingBarMinigame?.Dispose();
            _activeTimingBarMinigame = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Мини-игра «Синхронизация слепых зон» (Глава 6, StealthGrid) — тот же принцип
    /// слота, что у ActiveTimingBarMinigame выше, но исход как у WirePuzzle: провал — локальный
    /// Game Over с повтором той же мини-игры (см. StealthGridViewModel.RetryCommand), без
    /// перехода на другой узел.</summary>
    public StealthGridViewModel? ActiveStealthGrid
    {
        get => _activeStealthGrid;
        private set
        {
            if (ReferenceEquals(_activeStealthGrid, value))
                return;

            _activeStealthGrid?.Dispose();
            _activeStealthGrid = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Мини-игра «Сборка разрушенного кода» (Глава 8, CodeAssembly) — тот же принцип
    /// слота, что у ActiveTimingBarMinigame: провал ведёт на другой узел сцены
    /// (MinigameCue.FailureNodeId), а не на локальный повтор — ставки здесь одноразовые
    /// (father_file_found, см. CodeAssemblyViewModel).</summary>
    public CodeAssemblyViewModel? ActiveCodeAssembly
    {
        get => _activeCodeAssembly;
        private set
        {
            if (ReferenceEquals(_activeCodeAssembly, value))
                return;

            _activeCodeAssembly?.Dispose();
            _activeCodeAssembly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Мини-игра «Диагностика троса» (Глава 1, CableDiagnostics) — тот же принцип
    /// слота, что у остальных; единственная с тремя исходами (см. CableDiagnosticsViewModel) —
    /// успех продолжает Advance(), частичный успех и провал ведут каждый на свой узел сцены
    /// (MinigameCue.PartialNodeId/FailureNodeId), без повтора.</summary>
    public CableDiagnosticsViewModel? ActiveCableDiagnostics
    {
        get => _activeCableDiagnostics;
        private set
        {
            if (ReferenceEquals(_activeCableDiagnostics, value))
                return;

            _activeCableDiagnostics?.Dispose();
            _activeCableDiagnostics = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Мини-игра «Калибровка обходчика» (Глава 3, RingLock) — тот же принцип слота, что
    /// у ActiveCableDiagnostics: три исхода, частичный успех и провал ведут на свои узлы сцены
    /// (PartialNodeId/FailureNodeId). Взлом панели сюжетно гарантирован при любом исходе.</summary>
    public RingLockViewModel? ActiveRingLock
    {
        get => _activeRingLock;
        private set
        {
            if (ReferenceEquals(_activeRingLock, value))
                return;

            _activeRingLock?.Dispose();
            _activeRingLock = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Мини-игра «Скан лога панели» (Глава 3, LogScan) — тот же принцип слота, тоже три
    /// исхода (PartialNodeId/FailureNodeId), но провал здесь не критичен для сюжета: улика будет
    /// показана в любом случае, различается только подача.</summary>
    public LogScanViewModel? ActiveLogScan
    {
        get => _activeLogScan;
        private set
        {
            if (ReferenceEquals(_activeLogScan, value))
                return;

            _activeLogScan?.Dispose();
            _activeLogScan = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Интерактивный 3D-эпизод (Глава 2А «Ярмарка теней», Глава 6 «Шахты») — полноэкранная
    /// сцена с управлением Максом поверх новеллы. Маршрут игрока превращается в обычный выбор
    /// этого же узла (Episode3DLayout.RouteChoiceIds → GameSessionController.SelectChoice), исход —
    /// только в mg_*-флаги (ApplyMinigameOutcomeEffects). Варианты выбора узла при этом не
    /// показываются списком: игрок выбирает их ногами, а не кнопкой.</summary>
    public Episode3DViewModel? ActiveEpisode
    {
        get => _activeEpisode;
        private set
        {
            if (ReferenceEquals(_activeEpisode, value))
                return;

            _activeEpisode?.Dispose();
            _activeEpisode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>«Сборка досье» (Глава 8, сцена 8.2) — сама выбирает существующий вариант узла
    /// (сопоставить по смыслу / наугад) по результату сборки, см. DossierAssemblyLayout.</summary>
    public DossierAssemblyViewModel? ActiveDossierAssembly
    {
        get => _activeDossierAssembly;
        private set
        {
            if (ReferenceEquals(_activeDossierAssembly, value))
                return;

            _activeDossierAssembly?.Dispose();
            _activeDossierAssembly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    public BirthFloorComparisonViewModel? ActiveBirthFloorComparison
    {
        get => _activeBirthFloorComparison;
        private set
        {
            if (!SetProperty(ref _activeBirthFloorComparison, value))
                return;

            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    public ArchiveRecordVerificationViewModel? ActiveArchiveRecordVerification
    {
        get => _activeArchiveRecordVerification;
        private set
        {
            if (!SetProperty(ref _activeArchiveRecordVerification, value))
                return;

            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    public MarketWitnessRouteViewModel? ActiveMarketWitnessRoute
    {
        get => _activeMarketWitnessRoute;
        private set
        {
            if (!SetProperty(ref _activeMarketWitnessRoute, value))
                return;

            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    public MarketCardGameViewModel? ActiveMarketCardGame
    {
        get => _activeMarketCardGame;
        private set
        {
            if (!SetProperty(ref _activeMarketCardGame, value))
                return;

            OnPropertyChanged(nameof(IsAnyMinigameActive));
            RestartAutoAdvanceTimerIfNeeded();
        }
    }

    /// <summary>Общий флаг "идёт какая-то мини-игра" для команд/авточтения ниже — не важно,
    /// какая именно, узел задаёт не больше одной мини-игры за раз (см. RefreshFromCurrentNode).</summary>
    public bool IsAnyMinigameActive =>
        ActiveMinigame is not null || ActiveTimingBarMinigame is not null ||
        ActiveStealthGrid is not null || ActiveCodeAssembly is not null ||
        ActiveCableDiagnostics is not null || ActiveRingLock is not null || ActiveLogScan is not null ||
        ActiveEpisode is not null || ActiveDossierAssembly is not null || ActiveBirthFloorComparison is not null ||
        ActiveArchiveRecordVerification is not null || ActiveMarketWitnessRoute is not null || ActiveMarketCardGame is not null;

    /// <summary>Виден ли сейчас HUD-индикатор подозрения (Глава 5) — см. SuspicionMeterCue.</summary>
    public bool IsSuspicionMeterVisible
    {
        get => _isSuspicionMeterVisible;
        private set => SetProperty(ref _isSuspicionMeterVisible, value);
    }

    /// <summary>Текущее значение отслеживаемого флага — перечитывается из GameState при каждом
    /// RefreshFromCurrentNode, пока индикатор виден (значение меняют обычные setFlag в
    /// эффектах узлов/выборов, эта ViewModel их не применяет и не хранит сама).</summary>
    public int SuspicionMeterValue => _controller.State.GetFlag(_suspicionMeterFlagKey)?.IntegerValue ?? 0;

    public int SuspicionMeterMin => _suspicionMeterMin;
    public int SuspicionMeterMax => _suspicionMeterMax;
    public int SuspicionMeterYellowThreshold => _suspicionMeterYellowThreshold;
    public int SuspicionMeterRedThreshold => _suspicionMeterRedThreshold;

    /// <summary>0..1 — заполнение индикатора для привязки через FractionToWidthConverter.</summary>
    public double SuspicionMeterFraction
    {
        get
        {
            var span = _suspicionMeterMax - _suspicionMeterMin;
            if (span <= 0)
                return 0;

            return Math.Clamp((SuspicionMeterValue - _suspicionMeterMin) / (double)span, 0, 1);
        }
    }

    public bool IsSuspicionInGreenZone => SuspicionMeterValue < _suspicionMeterYellowThreshold;
    public bool IsSuspicionInYellowZone => SuspicionMeterValue >= _suspicionMeterYellowThreshold && SuspicionMeterValue < _suspicionMeterRedThreshold;
    public bool IsSuspicionInRedZone => SuspicionMeterValue >= _suspicionMeterRedThreshold;

    /// <summary>Идёт ли сейчас обратный отсчёт ответа на реплике (Глава 5) — см.
    /// StartChoiceTimeoutIfNeeded/StopChoiceTimeout.</summary>
    public bool IsChoiceTimerActive => _choiceTimeoutTimer is not null;

    /// <summary>0..1, убывает от 1 до 0 — для привязки прогресс-бара обратного отсчёта.</summary>
    public double ChoiceTimeoutFraction =>
        _choiceTimeoutTotalSeconds <= 0 ? 0 : Math.Clamp(_choiceTimeoutRemainingSeconds / _choiceTimeoutTotalSeconds, 0, 1);

    /// <summary>Быстрый переключатель авточтения прямо на экране новеллы — тот же персистентный
    /// AppSettings.AutoAdvanceEnabled, что и в SettingsView (одно состояние, два места доступа).</summary>
    public bool IsAutoAdvanceEnabled
    {
        get => _services.Settings.Current.AutoAdvanceEnabled;
        set
        {
            if (_services.Settings.Current.AutoAdvanceEnabled == value)
                return;

            _services.Settings.Current.AutoAdvanceEnabled = value;
            OnPropertyChanged();
            _services.Settings.NotifyChanged();
        }
    }

    public ICommand ContinueCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ToggleUiCommand { get; }
    public ICommand RestoreUiCommand { get; }
    public ICommand OpenPauseMenuCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand SelectPreviousChoiceCommand { get; }
    public ICommand SelectNextChoiceCommand { get; }
    public ICommand ConfirmSelectedChoiceCommand { get; }
    public ICommand ReplayVoiceCommand { get; }

    /// <summary>Показывать ли кнопку "повторить озвучку" у текущей реплики.</summary>
    public bool CanReplayVoice => _services.Audio.HasVoiceForCurrentLine;

    /// <summary>Резолвер изображений персонажей — читается View (NovelView.xaml.cs) при
    /// обработке CharacterCue, тот же принцип доступа, что и у Audio выше (методы принимают
    /// только id, не FrameworkElement, поэтому сервис можно безопасно держать в AppServices).</summary>
    public IAssetService Assets => _services.Asset;

    private void Advance()
    {
        if (_controller.HasEnded)
            return;

        AppendCurrentToLog();
        var previousSceneId = _controller.State.CurrentSceneId;
        _controller.Advance();
        RefreshFromCurrentNode(isSceneEntry: _controller.State.CurrentSceneId != previousSceneId);
    }

    /// <summary>Нестандартный исход мини-игры (провал у TimingBar/CodeAssembly, частичный успех
    /// у CableDiagnostics — см. соответствующие MinigameCue.FailureNodeId/PartialNodeId) — в
    /// отличие от Advance(), переходит не по nextNodeId текущего узла, а на отдельный узел той
    /// же сцены (GameSessionController.JumpToNode не умеет менять сцену).</summary>
    private void JumpToMinigameOutcomeNode(string nodeId)
    {
        if (_controller.HasEnded)
            return;

        AppendCurrentToLog();
        var previousSceneId = _controller.State.CurrentSceneId;
        _controller.JumpToNode(nodeId);
        RefreshFromCurrentNode(isSceneEntry: _controller.State.CurrentSceneId != previousSceneId);
    }

    private void ConfirmChoice(string choiceId, string choiceText)
    {
        StopChoiceTimeout();
        AppendCurrentToLog();

        AppendLogRecord(new DialogueLogRecord(
            DialogueLogEntryKind.ChoiceMade,
            _controller.CurrentScene.SceneId,
            _controller.CurrentNode.NodeId,
            "Вы",
            choiceText,
            DateTime.Now));

        var previousSceneId = _controller.State.CurrentSceneId;
        _controller.SelectChoice(choiceId);
        RefreshFromCurrentNode(isSceneEntry: _controller.State.CurrentSceneId != previousSceneId);
    }

    /// <summary>
    /// Пропуск уже прочитанного текста (requirement 5 настроек): быстро проходит узлы, которые
    /// были отмечены как просмотренные ДО нажатия (снимок ViewedNodeIds в начале, а не "живой"
    /// список — иначе любой узел считался бы "прочитанным" сразу после первого показа).
    /// Если AppSettings.AllowSkipUnread включён — пропускает и ещё не прочитанный текст.
    /// Всегда останавливается на узле с выбором, на концовке или по защитному лимиту шагов
    /// (на случай цикличного сценария). Промежуточные узлы не проигрывают анимации/звук —
    /// только финальный, достигнутый узел (см. единственный вызов RefreshFromCurrentNode внизу).
    /// </summary>
    private void Skip()
    {
        if (IsChoiceVisible || _controller.HasEnded)
            return;

        var viewedBeforeSkip = new HashSet<string>(_controller.State.ViewedNodeIds);
        var allowUnread = _services.Settings.Current.AllowSkipUnread;
        var sceneChanged = false;

        for (var step = 0; step < MaxSkipSteps; step++)
        {
            var wasAlreadyRead = viewedBeforeSkip.Contains(_controller.CurrentNode.NodeId);

            if ((!wasAlreadyRead && !allowUnread) || _controller.CurrentNode.Choices.Count > 0)
                break;

            AppendCurrentToLog();
            var previousSceneId = _controller.State.CurrentSceneId;
            _controller.Advance();

            if (_controller.State.CurrentSceneId != previousSceneId)
                sceneChanged = true;

            if (_controller.HasEnded)
                break;
        }

        RefreshFromCurrentNode(isSceneEntry: sceneChanged);
    }

    /// <summary>
    /// Технические узлы-развилки (например CH09_DEBTOR_D002_LEAK_GATE) задают несколько
    /// взаимоисключающих по condition вариантов, которые после фильтрации ChoiceProcessor'ом по
    /// текущему состоянию всегда схлопываются ровно в один доступный — то есть реального выбора
    /// у игрока в этой точке никогда не было, здесь только служебное ветвление сюжета (часто
    /// подписанное просто "Продолжить"). Показывать единственный вариант как список выбора —
    /// создавать иллюзию решения там, где его нет, и требовать лишний клик/Enter на пустом месте.
    /// Поэтому такие узлы проходятся автоматически через тот же SelectChoice, которым движок и
    /// так применяет любой выбор (эффекты/идемпотентность/история — без изменений), но без
    /// записи в ВИДИМЫЙ журнал реплик: тут намеренно нечего показывать игроку, текст узла вроде
    /// "Продолжить." не несёт сюжетной информации (ровно как сейчас не журналируются
    /// автоматически пройденные узлы мини-игр). Экран останавливается только там, где вариантов
    /// действительно несколько (настоящий вопрос) или их нет совсем (обычная реплика). Предел
    /// шагов — страховка на случай зацикленного сценария.
    /// </summary>
    private bool AutoResolveSingleChoiceGates(bool isSceneEntry)
    {
        for (var step = 0; step < MaxSkipSteps && !_controller.HasEnded; step++)
        {
            var available = _controller.GetAvailableChoices();
            if (available.Count != 1)
                break;

            var previousSceneId = _controller.State.CurrentSceneId;
            _controller.SelectChoice(available[0].ChoiceId);

            if (_controller.State.CurrentSceneId != previousSceneId)
                isSceneEntry = true;
        }

        return isSceneEntry;
    }

    private void RefreshFromCurrentNode(bool isSceneEntry)
    {
        StopChoiceTimeout();
        isSceneEntry = AutoResolveSingleChoiceGates(isSceneEntry);

        var node = _controller.CurrentNode;
        SpeakerName = ResolveSpeakerDisplayName(node.Speaker);
        LineText = node.Text;

        ApplySuspicionMeterCue(node.SuspicionMeterCue);
        if (IsSuspicionMeterVisible)
            NotifySuspicionMeterChanged();

        Choices.Clear();
        var available = _controller.GetAvailableChoices();
        var isEpisode = node.MinigameCue is { ChoosesForPlayer: true };

        if (available.Count > 0 && !isEpisode)
        {
            foreach (var choice in available)
            {
                var choiceId = choice.ChoiceId;
                var choiceText = choice.Text;
                Choices.Add(new ChoiceOptionViewModel(choiceText, onConfirm: () => ConfirmChoice(choiceId, choiceText)));
            }

            _selectedChoiceIndex = 0;
            Choices[0].IsSelected = true;
            IsChoiceVisible = true;
            StartChoiceTimeoutIfNeeded(node.ChoiceTimerCue, available);
        }
        else
        {
            IsChoiceVisible = false;
        }

        // Мини-игра узла (см. DialogueNode.MinigameCue) — успех просто продолжает сюжет
        // обычным Advance() по nextNodeId/nextSceneId этого узла, как будто её не было.
        // WirePuzzle/StealthGrid: провал остаётся внутри самой мини-игры (локальный повтор), не
        // трогая GameState. TimingBar/CodeAssembly: провал ведёт на другой узел ТЕКУЩЕЙ сцены
        // (MinigameCue.FailureNodeId) — настоящие сюжетные последствия, без повтора, см.
        // JumpToMinigameOutcomeNode ниже. CableDiagnostics — та же схема, но исходов три:
        // частичный успех ведёт на свой узел (PartialNodeId) отдельно от провала.
        var minigameCue = node.MinigameCue;

        ActiveMinigame = minigameCue is { Kind: MinigameKind.WirePuzzle }
            ? new WirePuzzleViewModel(minigameCue.GridSize, minigameCue.TimeLimitSeconds, onSuccess: Advance)
            : null;

        ActiveTimingBarMinigame = minigameCue is { Kind: MinigameKind.TimingBar }
            ? new TimingBarViewModel(
                minigameCue.TimingBarGreenZoneWidth,
                minigameCue.TimingBarSweepSeconds,
                onSuccess: Advance,
                onFailure: () => JumpToMinigameOutcomeNode(minigameCue.FailureNodeId!))
            : null;

        ActiveStealthGrid = minigameCue is { Kind: MinigameKind.StealthGrid }
            ? new StealthGridViewModel(minigameCue.StealthGrid!, onSuccess: Advance)
            : null;

        ActiveCodeAssembly = minigameCue is { Kind: MinigameKind.CodeAssembly }
            ? new CodeAssemblyViewModel(
                WithHardMode(minigameCue.CodeAssembly!),
                onSuccess: Advance,
                onFailure: () => JumpToMinigameOutcomeNode(minigameCue.FailureNodeId!))
            : null;

        ActiveCableDiagnostics = minigameCue is { Kind: MinigameKind.CableDiagnostics }
            ? new CableDiagnosticsViewModel(minigameCue.CableDiagnostics!, onResolved: outcome =>
            {
                switch (outcome)
                {
                    case CableDiagnosticsOutcome.Success:
                        Advance();
                        break;
                    case CableDiagnosticsOutcome.Partial:
                        JumpToMinigameOutcomeNode(minigameCue.PartialNodeId!);
                        break;
                    default:
                        JumpToMinigameOutcomeNode(minigameCue.FailureNodeId!);
                        break;
                }
            })
            : null;

        ActiveRingLock = minigameCue is { Kind: MinigameKind.RingLock }
            ? new RingLockViewModel(minigameCue.RingLock!, onResolved: outcome =>
            {
                switch (outcome)
                {
                    case RingLockOutcome.Success:
                        Advance();
                        break;
                    case RingLockOutcome.Partial:
                        JumpToMinigameOutcomeNode(minigameCue.PartialNodeId!);
                        break;
                    default:
                        JumpToMinigameOutcomeNode(minigameCue.FailureNodeId!);
                        break;
                }
            })
            : null;

        ActiveLogScan = minigameCue is { Kind: MinigameKind.LogScan }
            ? new LogScanViewModel(minigameCue.LogScan!, onResolved: outcome =>
            {
                switch (outcome)
                {
                    case LogScanOutcome.Success:
                        Advance();
                        break;
                    case LogScanOutcome.Partial:
                        JumpToMinigameOutcomeNode(minigameCue.PartialNodeId!);
                        break;
                    default:
                        JumpToMinigameOutcomeNode(minigameCue.FailureNodeId!);
                        break;
                }
            })
            : null;

        ActiveEpisode = minigameCue is { IsEpisode3D: true, Episode3D: not null }
            ? CreateEpisode(minigameCue)
            : null;

        ActiveDossierAssembly = minigameCue is { Kind: MinigameKind.DossierAssembly, DossierAssembly: not null }
            ? CreateDossierAssembly(minigameCue.DossierAssembly)
            : null;

        ActiveBirthFloorComparison = minigameCue?.Kind == MinigameKind.BirthFloorComparison
            ? new BirthFloorComparisonViewModel(Advance)
            : null;

        ActiveArchiveRecordVerification = minigameCue?.Kind == MinigameKind.ArchiveRecordVerification
            ? new ArchiveRecordVerificationViewModel(
                _controller.State.GetFlag("mg_ch8_archive_alert")?.BooleanValue == true,
                Advance, () => JumpToMinigameOutcomeNode(minigameCue.FailureNodeId!))
            : null;

        ActiveMarketWitnessRoute = minigameCue?.Kind == MinigameKind.MarketWitnessRoute
            ? new MarketWitnessRouteViewModel(Advance)
            : null;

        ActiveMarketCardGame = minigameCue?.Kind == MinigameKind.MarketCardGame
            ? new MarketCardGameViewModel(Advance, () => JumpToMinigameOutcomeNode(minigameCue.FailureNodeId!))
            : null;

        var scene = _controller.CurrentScene;

        if (isSceneEntry)
        {
            // Фоновая музыка и эмбиент привязаны к сцене (requirement 1, 2) — плавная смена
            // при входе в новую сцену (requirement 6); отсутствующий ресурс (null или файл
            // не найден) — AudioService сам корректно гасит предыдущий трек без ошибки.
            _ = _services.Audio.PlayMusicAsync(scene.Music);
            _ = _services.Audio.PlayAmbienceAsync(scene.Ambience);
        }

        // Голос — синхронно с показом реплики (requirement: запускается синхронно с
        // соответствующей репликой); всегда останавливает предыдущий (requirement: остановка
        // предыдущей реплики). SFX узла — одноразовые звуки при входе (requirement 3).
        _services.Audio.PlayVoice(node.VoiceClip);
        OnPropertyChanged(nameof(CanReplayVoice));

        foreach (var sfxId in node.SoundEffects)
            _services.Audio.PlaySfx(sfxId);

        var ambientAllowed = _services.Settings.Current.AnimationLevel != AnimationLevel.Off;

        var cues = new NodeAnimationCues(
            Speaker: node.Speaker,
            CharacterCues: node.CharacterCues,
            CameraCue: node.CameraCue,
            ScreenFadeCue: node.ScreenFadeCue,
            IsSceneEntry: isSceneEntry,
            Background: isSceneEntry ? scene.Background : null,
            BackgroundTransition: isSceneEntry ? scene.BackgroundTransition : null,
            AmbientEffect: isSceneEntry && ambientAllowed ? scene.AmbientEffect : AmbientEffectKind.None);

        LastAnimationCues = cues;
        AnimationCuesReady?.Invoke(this, cues);

        RestartAutoAdvanceTimerIfNeeded();
    }

    /// <summary>
    /// Создаёт мост 3D-эпизода для узла. Маршрут фиксируется либо сразу при входе в проход
    /// (CommitRouteOnEntry — Глава 6, Выбор 6: эффекты H±10/tool_lost применяются в тот же момент),
    /// либо по завершении (Глава 2А). По завершении: сначала mg_*-флаги исхода, затем — узел исхода
    /// (если задан, напр. тревога в Главе 2А), иначе выбор маршрута, иначе (маршрут уже применён) —
    /// просто показ узла, на котором движок уже стоит.
    /// </summary>
    private Episode3DViewModel CreateEpisode(MinigameCue cue)
    {
        var layout = cue.Episode3D!;
        var episodeNodeId = _controller.CurrentNode.NodeId;
        var routeApplied = false;

        void ApplyRoute(string routeKey)
        {
            if (routeApplied || !layout.RouteChoiceIds.TryGetValue(routeKey, out var choiceId))
                return;
            routeApplied = true;
            var choiceText = _controller.CurrentNode.Choices.First(c => c.ChoiceId == choiceId).Text;
            AppendCurrentToLog();
            AppendLogRecord(new DialogueLogRecord(
                DialogueLogEntryKind.ChoiceMade, _controller.CurrentScene.SceneId, _controller.CurrentNode.NodeId,
                "Вы", choiceText, DateTime.Now));
            _controller.SelectChoice(choiceId);
        }

        return new Episode3DViewModel(
            cue.Kind,
            _services.Settings,
            onRouteCommitted: routeKey =>
            {
                if (layout.CommitRouteOnEntry && _controller.CurrentNode.NodeId == episodeNodeId)
                    ApplyRoute(routeKey);
            },
            onCompleted: (outcomeKey, routeKey) =>
            {
                if (_controller.HasEnded)
                    return;

                if (layout.OutcomeEffects.TryGetValue(outcomeKey, out var effects))
                    _controller.ApplyMinigameOutcomeEffects(effects);

                var previousSceneId = _controller.State.CurrentSceneId;
                if (!routeApplied && layout.OutcomeNodeIds.TryGetValue(outcomeKey, out var outcomeNode))
                {
                    AppendCurrentToLog();
                    _controller.JumpToNode(outcomeNode);
                }
                else if (!routeApplied && layout.RouteChoiceIds.Count == 0)
                {
                    // эпизод без маршрутов стоит на обычном узле-реплике (Глава 8.1) — сюжет
                    // просто продолжается по его nextNodeId/nextSceneId
                    AppendCurrentToLog();
                    _controller.Advance();
                }
                else if (!routeApplied)
                {
                    // маршрут не зафиксирован при входе (или эпизод завершён без входа в проход) —
                    // применяем выбор маршрута сейчас; неизвестный ключ — первый маршрут по данным
                    var key = layout.RouteChoiceIds.ContainsKey(routeKey) ? routeKey : layout.RouteChoiceIds.Keys.First();
                    ApplyRoute(key);
                }

                RefreshFromCurrentNode(isSceneEntry: _controller.State.CurrentSceneId != previousSceneId);
            },
            onExitToMenu: ConfirmExitToMenu);
    }

    /// <summary>«Жёсткий режим» CodeAssembly (Глава 8.3): если поднят флаг HardModeFlag (тревога в
    /// архиве), берутся более строгие время и лимит ошибок из тех же данных сценария.</summary>
    private CodeAssemblyLayout WithHardMode(CodeAssemblyLayout layout)
    {
        if (layout.HardModeFlag is not { } flag || _controller.State.GetFlag(flag)?.BooleanValue != true)
            return layout;

        return new CodeAssemblyLayout
        {
            Pairs = layout.Pairs,
            TimeLimitSeconds = layout.HardTimeLimitSeconds,
            MaxErrors = layout.HardMaxErrors,
            Decoys = layout.Decoys,
            DecaySeconds = layout.DecaySeconds,
            MaxDecayFraction = layout.MaxDecayFraction,
            ErrorTimePenalty = layout.ErrorTimePenalty,
            ShuffleSeconds = layout.ShuffleSeconds,
            ComboWindowSeconds = layout.ComboWindowSeconds,
            ComboBonusSeconds = layout.ComboBonusSeconds
        };
    }

    private DossierAssemblyViewModel CreateDossierAssembly(DossierAssemblyLayout layout)
    {
        var nodeId = _controller.CurrentNode.NodeId;
        return new DossierAssemblyViewModel(layout, success =>
        {
            if (_controller.HasEnded || _controller.CurrentNode.NodeId != nodeId)
                return;

            _controller.ApplyMinigameOutcomeEffects(success ? layout.SuccessEffects : layout.FailureEffects);
            var choiceId = success ? layout.SuccessChoiceId : layout.FailureChoiceId;
            var choiceText = _controller.CurrentNode.Choices.First(c => c.ChoiceId == choiceId).Text;
            ConfirmChoice(choiceId, choiceText);
        });
    }

    /// <summary>Show — запоминает, какой флаг показывать и по каким порогам красить (Глава 5);
    /// Hide — прячет. Null (узел не задаёт ремарку) намеренно ничего не меняет — индикатор,
    /// однажды показанный, остаётся виден на последующих узлах до явного Hide, как и
    /// CharacterCue.Enter/Exit. Значение/зону всегда перечитывает отдельно — NotifySuspicionMeterChanged
    /// ниже, вызывается безусловно при каждом RefreshFromCurrentNode, пока индикатор виден, —
    /// потому что флаг меняют эффекты ЛЮБОГО узла/выбора, не только тех, что несут эту ремарку.</summary>
    private void ApplySuspicionMeterCue(SuspicionMeterCue? cue)
    {
        if (cue is null)
            return;

        if (cue.Kind == SuspicionMeterKind.Hide)
        {
            IsSuspicionMeterVisible = false;
            return;
        }

        _suspicionMeterFlagKey = cue.FlagKey;
        _suspicionMeterMin = cue.MinValue;
        _suspicionMeterMax = cue.MaxValue;
        _suspicionMeterYellowThreshold = cue.YellowThreshold;
        _suspicionMeterRedThreshold = cue.RedThreshold;
        IsSuspicionMeterVisible = true;

        OnPropertyChanged(nameof(SuspicionMeterMin));
        OnPropertyChanged(nameof(SuspicionMeterMax));
        OnPropertyChanged(nameof(SuspicionMeterYellowThreshold));
        OnPropertyChanged(nameof(SuspicionMeterRedThreshold));
    }

    private void NotifySuspicionMeterChanged()
    {
        OnPropertyChanged(nameof(SuspicionMeterValue));
        OnPropertyChanged(nameof(SuspicionMeterFraction));
        OnPropertyChanged(nameof(IsSuspicionInGreenZone));
        OnPropertyChanged(nameof(IsSuspicionInYellowZone));
        OnPropertyChanged(nameof(IsSuspicionInRedZone));
    }

    /// <summary>Запускает обратный отсчёт ответа на текущем узле, если он его задаёт (Глава 5,
    /// ChoiceTimerCue), настройка не отключена, и целевой для таймаута выбор реально доступен
    /// прямо сейчас (мог отфильтроваться условием) — иначе таймер просто не запускается, никакой
    /// деградации. RefreshFromCurrentNode уже вызвал StopChoiceTimeout() до этого, так что здесь
    /// заведомо нет предыдущего таймера.</summary>
    private void StartChoiceTimeoutIfNeeded(ChoiceTimerCue? cue, IReadOnlyList<ChoiceDefinition> available)
    {
        if (cue is null || _services.Settings.Current.DisableChoiceTimers)
            return;

        if (available.All(c => c.ChoiceId != cue.TimeoutChoiceId))
            return;

        _choiceTimeoutChoiceId = cue.TimeoutChoiceId;
        _choiceTimeoutTotalSeconds = cue.TimeoutSeconds;
        _choiceTimeoutRemainingSeconds = cue.TimeoutSeconds;
        OnPropertyChanged(nameof(ChoiceTimeoutFraction));
        OnPropertyChanged(nameof(IsChoiceTimerActive));

        _choiceTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _choiceTimeoutTimer.Tick += OnChoiceTimeoutTick;
        _choiceTimeoutTimer.Start();
    }

    private void OnChoiceTimeoutTick(object? sender, EventArgs e)
    {
        _choiceTimeoutRemainingSeconds -= 0.1;
        OnPropertyChanged(nameof(ChoiceTimeoutFraction));

        if (_choiceTimeoutRemainingSeconds > 0)
            return;

        var choiceId = _choiceTimeoutChoiceId;
        var choiceText = _controller.CurrentNode.Choices.FirstOrDefault(c => c.ChoiceId == choiceId)?.Text;
        StopChoiceTimeout();

        if (choiceId is not null && choiceText is not null)
            ConfirmChoice(choiceId, choiceText);
    }

    private void StopChoiceTimeout()
    {
        if (_choiceTimeoutTimer is null)
            return;

        _choiceTimeoutTimer.Stop();
        _choiceTimeoutTimer.Tick -= OnChoiceTimeoutTick;
        _choiceTimeoutTimer = null;
        _choiceTimeoutChoiceId = null;
        OnPropertyChanged(nameof(IsChoiceTimerActive));
    }

    /// <summary>Подпись говорящего на русском — requirement "имя говорящего": основной состав
    /// берёт отображаемое имя из CharacterDefinition (Data/Characters/characters.json, уже на
    /// русском), рассказчик и второстепенные безликие роли — из MinorSpeakerDisplayNames; сырой
    /// служебный id (напр. "narrator"/"max_orlov") никогда не показывается игроку напрямую.</summary>
    private string ResolveSpeakerDisplayName(string speakerId)
    {
        if (MinorSpeakerDisplayNames.TryGetValue(speakerId, out var minorName))
            return minorName;

        var character = _services.Asset.GetCharacterDefinition(speakerId);
        return character?.DisplayName ?? speakerId;
    }

    private void MoveChoiceSelection(int delta)
    {
        if (Choices.Count == 0)
            return;

        var next = (_selectedChoiceIndex + delta + Choices.Count) % Choices.Count;
        MoveSelectionTo(next);
    }

    private void MoveSelectionTo(int index)
    {
        if (Choices.Count == 0)
            return;

        _selectedChoiceIndex = Math.Clamp(index, 0, Choices.Count - 1);

        for (var i = 0; i < Choices.Count; i++)
            Choices[i].IsSelected = i == _selectedChoiceIndex;
    }

    private void AppendCurrentToLog()
    {
        if (string.IsNullOrWhiteSpace(LineText))
            return;

        AppendLogRecord(new DialogueLogRecord(
            DialogueLogEntryKind.Line,
            _controller.CurrentScene.SceneId,
            _controller.CurrentNode.NodeId,
            SpeakerName,
            LineText,
            DateTime.Now));
    }

    /// <summary>Единая точка записи в журнал — требование "журнал уже прочитанных реплик и
    /// история диалогов": пишет и в GameState.DialogueLog (переживает сохранение/загрузку),
    /// и в наблюдаемую коллекцию для текущего отображения (LogPanelControl).</summary>
    private void AppendLogRecord(DialogueLogRecord record)
    {
        _controller.State.AppendToDialogueLog(record);
        LogEntries.Add(ToDisplayEntry(record));
    }

    private static DialogueLogEntry ToDisplayEntry(DialogueLogRecord record) => record.Kind == DialogueLogEntryKind.ChoiceMade
        ? new DialogueLogEntry(record.Speaker, $"➤ {record.Text}")
        : new DialogueLogEntry(record.Speaker, record.Text);

    private int CurrentPlayTimeSeconds => _baselinePlayTimeSeconds + (int)_sessionStopwatch.Elapsed.TotalSeconds;

    /// <summary>Снимок текущего прохождения для записи в слот — требование "сохранение должно
    /// включать главу/сцену/узел/переменные/флаги/концовку/историю выборов/состояние сюжетных
    /// событий", всё это уже внутри GameState (см. его комментарии).</summary>
    private SaveSlotData BuildSnapshot() => new()
    {
        SaveVersion = _services.SaveService.CurrentSaveVersion,
        Timestamp = DateTime.Now,
        PlayTimeSeconds = CurrentPlayTimeSeconds,
        ChapterId = _controller.State.CurrentChapterId,
        SceneId = _controller.State.CurrentSceneId,
        NodeId = _controller.State.CurrentNodeId,
        CurrentEndingId = _controller.State.CurrentEndingId,
        LocationSummary = BuildLocationSummary(),
        GameState = _controller.State
    };

    private string BuildLocationSummary()
    {
        const int maxLength = 90;
        var text = LineText.Length > maxLength ? LineText[..maxLength] + "…" : LineText;
        return string.IsNullOrWhiteSpace(SpeakerName) ? text : $"{SpeakerName}: {text}";
    }

    private void OnAutoSaveRequested(AutoSaveTrigger trigger)
    {
        try
        {
            _services.SaveService.SaveAutoSave(BuildSnapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Автосохранение не должно прерывать игровую сессию — просто не сохранилось
            // на этот раз, следующий триггер (вход в следующую главу) попробует снова.
        }
    }

    private void OpenSaveScreen() =>
        _services.Navigation.NavigateTo(new SaveLoadViewModel(
            _services,
            SaveLoadMode.Save,
            currentSnapshotProvider: BuildSnapshot,
            onSlotLoaded: _ => { },
            onBack: () => _services.Navigation.NavigateTo(this)));

    private void OpenLoadScreen() =>
        _services.Navigation.NavigateTo(new SaveLoadViewModel(
            _services,
            SaveLoadMode.Load,
            currentSnapshotProvider: null,
            onSlotLoaded: data => _services.Navigation.NavigateTo(new NovelViewModel(_services, data)),
            onBack: () => _services.Navigation.NavigateTo(this)));

    private void OpenSettingsScreen() =>
        _services.Navigation.NavigateTo(new SettingsViewModel(_services, onBack: () => _services.Navigation.NavigateTo(this)));

    private void ConfirmExitToMenu() =>
        _services.Confirmation.RequestConfirmation(new ConfirmationRequest(
            "Выйти в главное меню?",
            "Несохранённый прогресс текущей сессии будет потерян.",
            OnConfirm: () => _ = ReturnToMainMenuAsync(),
            IsDanger: true));

    private void ConfirmExitApplication() =>
        _services.Confirmation.RequestConfirmation(new ConfirmationRequest(
            "Выйти из игры?",
            "Все несохранённые данные текущей сессии будут потеряны.",
            OnConfirm: () => System.Windows.Application.Current.Shutdown(),
            IsDanger: true));

    private async Task ReturnToMainMenuAsync()
    {
        _services.ScreenTransition.ShowLoading("Возврат в меню…");
        _services.Audio.StopAll();
        StopAutoAdvanceTimer();
        _services.Settings.Changed -= OnSettingsChanged;
        await Task.Delay(500);
        _services.Navigation.NavigateTo(new MainMenuViewModel(_services));
        _services.ScreenTransition.HideLoading();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Старые экземпляры NovelViewModel (из прошлой игры/сейва) остаются подписанными на
        // ISettingsStore.Changed (singleton на весь процесс) — отписываемся, как только эта
        // ViewModel больше не текущий экран, чтобы не копились "осиротевшие" подписки и не
        // мутировали чужое, уже неактуальное состояние движка.
        if (_services.Navigation.CurrentViewModel != this)
        {
            _services.Settings.Changed -= OnSettingsChanged;
            StopAutoAdvanceTimer();
            return;
        }

        OnPropertyChanged(nameof(IsAutoAdvanceEnabled));
        RestartAutoAdvanceTimerIfNeeded();
    }

    /// <summary>Авточтение (requirement "автоматическое продолжение диалогов" + "скорость
    /// автоматического режима"): таймер перезапускается после каждого показанного узла и
    /// всегда останавливается на узле с выбором/в журнале/в паузе/на концовке.</summary>
    private void RestartAutoAdvanceTimerIfNeeded()
    {
        StopAutoAdvanceTimer();

        if (_controller.HasEnded || IsChoiceVisible || IsPauseMenuVisible || IsLogVisible || IsAnyMinigameActive)
            return;

        var settings = _services.Settings.Current;
        if (!settings.AutoAdvanceEnabled)
            return;

        _autoAdvanceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(0.2, settings.AutoAdvanceDelaySeconds))
        };

        _autoAdvanceTimer.Tick += (_, _) =>
        {
            StopAutoAdvanceTimer();

            if (_services.Navigation.CurrentViewModel == this && !IsChoiceVisible && !_controller.HasEnded)
                Advance();
        };

        _autoAdvanceTimer.Start();
    }

    private void StopAutoAdvanceTimer()
    {
        if (_autoAdvanceTimer is null)
            return;

        _autoAdvanceTimer.Stop();
        _autoAdvanceTimer = null;
    }
}
