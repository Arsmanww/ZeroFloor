namespace ZeroFloor.Models;

public enum MinigameKind
{
    WirePuzzle,
    TimingBar,
    StealthGrid,
    CodeAssembly,
    CableDiagnostics,
    RingLock,
    LogScan,

    /// <summary>Глава 2А «Ярмарка теней»: 3D-проникновение через рынок мимо патрульного дрона
    /// (управление Максом WASD), см. Episode3DLayout.</summary>
    MarketStealth3D,

    /// <summary>Глава 6 «Шахты»: 3D-погоня по техническим стволам с физической развилкой
    /// маршрута (Выбор 6), см. Episode3DLayout.</summary>
    ShaftsChase3D,

    /// <summary>Глава 8 «Архив Смотрителей», сцена 8.1: 3D-проникновение мимо поворотных камер
    /// вместе с Виктором, см. Episode3DLayout.</summary>
    ArchiveStealth3D,

    /// <summary>Глава 8, сцена 8.2: «Сборка досье» Виктора и Сони, см. DossierAssemblyLayout.</summary>
    DossierAssembly,

    BirthFloorComparison,
    ArchiveRecordVerification,
    MarketWitnessRoute,
    MarketCardGame
}

/// <summary>
/// Сценарная "режиссёрская ремарка" узла, запускающая мини-игру поверх окна новеллы —
/// чистые данные, как CharacterCue/CameraCue/ScreenFadeCue: Engine их не читает и не
/// валидирует (кроме целостности <see cref="FailureNodeId"/>/<see cref="PartialNodeId"/>,
/// <see cref="StealthGrid"/>, <see cref="CodeAssembly"/>, <see cref="CableDiagnostics"/>,
/// <see cref="RingLock"/> и <see cref="LogScan"/>, см. ScenarioValidator), не влияют на
/// GameState/переходы напрямую — Presentation-слой решает исход:
/// - WirePuzzle (см. WirePuzzleViewModel): успех — обычный Advance() по nextNodeId/
///   nextSceneId узла; провал — локальный повтор той же мини-игры без каких-либо
///   последствий для GameState (не ломает сюжетный флоучарт, см. CLAUDE.md).
/// - TimingBar (см. TimingBarViewModel, Глава 2 «Кража обходчика»): успех — тот же
///   Advance() по nextNodeId/nextSceneId узла; провал ведёт на другой узел той же сцены —
///   <see cref="FailureNodeId"/> — с реальными сюжетными последствиями, а не повтором.
/// - StealthGrid (см. StealthGridViewModel, Глава 6 «Синхронизация слепых зон»): успех (дошёл до
///   выхода) — тот же Advance(); провал (засветился в конусе дрона) — локальный Game Over с
///   повтором той же мини-игры с начала, как у WirePuzzle, без последствий для GameState.
/// - CodeAssembly (см. CodeAssemblyViewModel, Глава 8 «Сборка разрушенного кода»): успех — тот же
///   Advance(); провал (таймер истёк или превышен лимит ошибок) ведёт, как у TimingBar, на
///   другой узел той же сцены — <see cref="FailureNodeId"/> — без повтора: ставки одноразовые
///   (см. CLAUDE.md §1/§2 — здесь завязан father_file_found, по отдельному решению автора).
/// - CableDiagnostics (см. CableDiagnosticsViewModel, Глава 1 «Диагностика троса»): три исхода,
///   не два — успех по-прежнему обычный Advance(), а "частичный успех" ведёт на
///   <see cref="PartialNodeId"/> и провал — на <see cref="FailureNodeId"/>; оба узла той же
///   сцены, без повтора (одна попытка). Ни один исход не меняет Выбор 1/H/доступность главы —
///   только флаги mg_ch01_cable_result/mg_ch01_cable_stable, не используемые для концовок.
/// - RingLock (см. RingLockViewModel, Глава 3 «Калибровка обходчика»): тоже три исхода, та же
///   схема PartialNodeId/FailureNodeId. Взлом панели сюжетно гарантирован при любом исходе —
///   провал лишь меняет, насколько аварийно он прошёл (флаг mg_ch03_hack_result); shard_obtained
///   ставится только отдельным текстовым Выбором 3 дальше по сценарию, этой мини-игрой не трогается.
/// - LogScan (см. LogScanViewModel, Глава 3 «Скан лога панели»): тоже три исхода, та же схема
///   PartialNodeId/FailureNodeId, что у CableDiagnostics/RingLock. Все три узла в итоге сходятся
///   обратно к одному и тому же тексту лога и Выбору 3 — провал убирает второстепенную деталь
///   подачи, а не саму улику; флаг mg_ch03_log_result не приравнивается к shard_obtained.
/// - MarketStealth3D / ShaftsChase3D (см. Episode3DViewModel, Главы 2А и 6): полноценные 3D-эпизоды
///   с управлением Максом. Маршрут игрока превращается в обычный выбор узла (Episode3DLayout.
///   RouteChoiceIds), исход — флаги и сдвиги шкал до ±10; провал никогда не блокирует главу.
/// </summary>
public sealed class MinigameCue
{
    public required MinigameKind Kind { get; init; }

    /// <summary>WirePuzzle: ширина сетки (количество столбцов); высота фиксирована на 3 строки.</summary>
    public int GridSize { get; init; } = 5;

    /// <summary>WirePuzzle: секунды до провала (истечение шкалы патруля).</summary>
    public int TimeLimitSeconds { get; init; } = 12;

    /// <summary>TimingBar: доля (0..1) полосы, занятая зелёной зоной (жёлтые зоны — фиксированный
    /// буфер по обе стороны от неё, остальное — красная зона).</summary>
    public double TimingBarGreenZoneWidth { get; init; } = 0.16;

    /// <summary>TimingBar: секунды на один проход ползунка от края до края полосы — чем меньше,
    /// тем быстрее и труднее попасть в зелёную зону.</summary>
    public double TimingBarSweepSeconds { get; init; } = 0.9;

    /// <summary>Id узла той же сцены, на который ведёт провал — обязателен для TimingBar,
    /// CodeAssembly, CableDiagnostics, RingLock и LogScan (у всех провал меняет сюжет, а не
    /// просто повторяет мини-игру), см. ScenarioValidator.</summary>
    public string? FailureNodeId { get; init; }

    /// <summary>StealthGrid: разметка сетки/укрытий/маршрутов дронов — обязательна для
    /// StealthGrid, см. ScenarioValidator.</summary>
    public StealthGridLayout? StealthGrid { get; init; }

    /// <summary>CodeAssembly: пары «чистый фрагмент/испорченная подсказка слота», лимит времени
    /// и ошибок — обязательна для CodeAssembly, см. ScenarioValidator.</summary>
    public CodeAssemblyLayout? CodeAssembly { get; init; }

    /// <summary>CableDiagnostics: натяжение/зоны/таймеры троса — обязательна для
    /// CableDiagnostics, см. ScenarioValidator.</summary>
    public CableDiagnosticsLayout? CableDiagnostics { get; init; }

    /// <summary>Id узла той же сцены для "частичного успеха" — используется CableDiagnostics,
    /// RingLock и LogScan (мини-игры с тремя исходами, а не двумя), обязателен для них же, см.
    /// ScenarioValidator.</summary>
    public string? PartialNodeId { get; init; }

    /// <summary>RingLock: число насечек/скорости/допуски обоих колец — обязательна для RingLock,
    /// см. ScenarioValidator.</summary>
    public RingLockLayout? RingLock { get; init; }

    /// <summary>LogScan: строки лога/цель/тайминг прокрутки — обязательна для LogScan, см.
    /// ScenarioValidator.</summary>
    public LogScanLayout? LogScan { get; init; }

    /// <summary>MarketStealth3D/ShaftsChase3D: связь 3D-эпизода со сценарным графом (маршруты →
    /// выборы узла, исходы → mg_*-флаги/узлы) — обязательна для обоих, см. ScenarioValidator.</summary>
    public Episode3DLayout? Episode3D { get; init; }

    /// <summary>Интерактивный 3D-эпизод с прямым управлением персонажем (а не оверлей-головоломка).</summary>
    public bool IsEpisode3D => Kind is MinigameKind.MarketStealth3D or MinigameKind.ShaftsChase3D or MinigameKind.ArchiveStealth3D;

    /// <summary>DossierAssembly: обрывки, время, выборы узла для успеха/провала.</summary>
    public DossierAssemblyLayout? DossierAssembly { get; init; }

    /// <summary>Мини-игра сама выбирает один из вариантов узла — список выбора не показывается.</summary>
    public bool ChoosesForPlayer => IsEpisode3D || Kind == MinigameKind.DossierAssembly;
}
