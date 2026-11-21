using System.Linq;
using ZeroFloor.Models;

namespace ZeroFloor.Engine;

/// <summary>
/// Эагерная (load-time) проверка целостности всего загруженного сценария: уникальность
/// идентификаторов сцен/узлов/выборов, корректность всех переходов, наличие текста
/// реплики/говорящего, структурная корректность условий и эффектов. Вызывается один раз
/// сразу после загрузки (ScenarioRepository), чтобы ошибка сценарных данных обнаруживалась
/// при старте, а не посреди прохождения игроком. Дополняет (не заменяет) защитные проверки
/// времени выполнения в ChoiceProcessor/EffectApplier/ConditionEvaluator — те же инварианты,
/// проверенные на двух этапах жизненного цикла для защиты в глубину.
/// </summary>
public sealed class ScenarioValidator
{
    public void Validate(IReadOnlyCollection<SceneDefinition> scenes)
    {
        var sceneIds = new HashSet<string>();

        foreach (var scene in scenes)
        {
            if (string.IsNullOrWhiteSpace(scene.SceneId))
                throw new ScenarioValidationException("Обнаружена сцена с пустым идентификатором.");

            if (!sceneIds.Add(scene.SceneId))
                throw new ScenarioValidationException($"Дублирующийся идентификатор сцены: '{scene.SceneId}'.");
        }

        foreach (var scene in scenes)
            ValidateScene(scene, sceneIds);
    }

    private void ValidateScene(SceneDefinition scene, HashSet<string> sceneIds)
    {
        if (scene.Nodes.Count == 0)
            throw new ScenarioValidationException($"Сцена '{scene.SceneId}' не содержит ни одного узла.");

        var nodeIds = new HashSet<string>();
        foreach (var node in scene.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
                throw new ScenarioValidationException($"Сцена '{scene.SceneId}' содержит узел с пустым идентификатором.");

            if (!nodeIds.Add(node.NodeId))
                throw new ScenarioValidationException(
                    $"Сцена '{scene.SceneId}': дублирующийся идентификатор узла '{node.NodeId}'.");
        }

        foreach (var node in scene.Nodes)
            ValidateNode(scene, node, nodeIds, sceneIds);
    }

    private void ValidateNode(SceneDefinition scene, DialogueNode node, HashSet<string> nodeIds, HashSet<string> sceneIds)
    {
        if (string.IsNullOrWhiteSpace(node.Speaker))
            throw new ScenarioValidationException(Where(scene, node, null) + ": не указан говорящий (speaker).");

        if (string.IsNullOrWhiteSpace(node.Text))
            throw new ScenarioValidationException(Where(scene, node, null) + ": отсутствует текст реплики.");

        foreach (var effect in node.Effects)
            ValidateEffect(scene, node, null, effect);

        ValidateMinigameCue(scene, node, nodeIds);
        ValidateSuspicionMeterCue(scene, node);
        ValidateChoiceTimerCue(scene, node);

        if (node.Choices.Count > 0)
        {
            var choiceIds = new HashSet<string>();
            foreach (var choice in node.Choices)
                ValidateChoice(scene, node, choice, choiceIds, nodeIds, sceneIds);
            return;
        }

        // Узел без вариантов выбора обязан однозначно задавать, что дальше.
        var destinationCount = CountNonEmpty(node.NextNodeId) + CountNonEmpty(node.NextSceneId);
        if (destinationCount != 1)
            throw new ScenarioValidationException(
                Where(scene, node, null) +
                $": без выборов узел должен задавать ровно один переход (nextNodeId или nextSceneId), а указано: {destinationCount}.");

        if (node.NextNodeId is not null && !nodeIds.Contains(node.NextNodeId))
            throw new ScenarioValidationException(
                Where(scene, node, null) + $": nextNodeId ссылается на несуществующий узел '{node.NextNodeId}'.");

        if (node.NextSceneId is not null && !sceneIds.Contains(node.NextSceneId))
            throw new ScenarioValidationException(
                Where(scene, node, null) + $": nextSceneId ссылается на несуществующую сцену '{node.NextSceneId}'.");
    }

    /// <summary>SuspicionMeterCue — чистая презентация (см. его комментарий), но диапазон/пороги
    /// обязаны быть внутренне согласованы, иначе индикатор на экране будет врать о зонах.</summary>
    private void ValidateSuspicionMeterCue(SceneDefinition scene, DialogueNode node)
    {
        if (node.SuspicionMeterCue is not { Kind: SuspicionMeterKind.Show } cue)
            return;

        if (string.IsNullOrWhiteSpace(cue.FlagKey))
            throw new ScenarioValidationException(Where(scene, node, null) + ": suspicionMeterCue не задаёт flagKey.");

        if (cue.MinValue >= cue.MaxValue)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": suspicionMeterCue.minValue должен быть меньше maxValue.");

        if (cue.YellowThreshold < cue.MinValue || cue.YellowThreshold > cue.MaxValue
            || cue.RedThreshold < cue.MinValue || cue.RedThreshold > cue.MaxValue
            || cue.YellowThreshold > cue.RedThreshold)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": suspicionMeterCue: пороги должны лежать в [minValue, maxValue] и yellowThreshold <= redThreshold.");
    }

    /// <summary>ChoiceTimerCue — таймаут обязан целиться в реально существующий выбор ЭТОГО же
    /// узла: Engine его не читает (таймер — целиком Presentation), но опечатка в
    /// timeoutChoiceId незаметно отключила бы автовыбор только во время прохождения.</summary>
    private void ValidateChoiceTimerCue(SceneDefinition scene, DialogueNode node)
    {
        if (node.ChoiceTimerCue is not { } cue)
            return;

        if (cue.TimeoutSeconds <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": choiceTimerCue.timeoutSeconds должен быть положительным.");

        if (node.Choices.Count == 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": choiceTimerCue задан на узле без выборов.");

        if (node.Choices.All(c => c.ChoiceId != cue.TimeoutChoiceId))
            throw new ScenarioValidationException(
                Where(scene, node, null) +
                $": choiceTimerCue.timeoutChoiceId '{cue.TimeoutChoiceId}' не соответствует ни одному выбору этого узла.");
    }

    /// <summary>Часть MinigameCue, которую проверяет Engine (остальное — чистая презентация, см.
    /// комментарий MinigameCue): TimingBar/CodeAssembly/CableDiagnostics/RingLock/LogScan обязаны
    /// задавать FailureNodeId (CableDiagnostics/RingLock/LogScan — ещё и PartialNodeId), ведущий
    /// на существующий узел ТОЙ ЖЕ сцены — GameSessionController.JumpToNode не умеет переходить
    /// между сценами (см. DialogueManager.GoToNode). StealthGrid/CodeAssembly/CableDiagnostics/
    /// RingLock/LogScan обязаны задавать корректные данные — их читает только соответствующая
    /// ViewModel, но индекс за границами сетки или пустой список пар/строк обрушил бы игру во
    /// время прохождения, а не при загрузке, поэтому базовая целостность проверяется здесь же,
    /// эагерно.</summary>
    private void ValidateMinigameCue(SceneDefinition scene, DialogueNode node, HashSet<string> nodeIds)
    {
        if (node.MinigameCue is not { } cue)
            return;

        if (cue.Kind is MinigameKind.TimingBar or MinigameKind.CodeAssembly or MinigameKind.CableDiagnostics
            or MinigameKind.RingLock or MinigameKind.LogScan or MinigameKind.ArchiveRecordVerification
            or MinigameKind.MarketCardGame)
        {
            if (string.IsNullOrWhiteSpace(cue.FailureNodeId))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": мини-игра {cue.Kind} не задаёт failureNodeId.");

            if (!nodeIds.Contains(cue.FailureNodeId))
                throw new ScenarioValidationException(
                    Where(scene, node, null) +
                    $": failureNodeId мини-игры ссылается на несуществующий в этой сцене узел '{cue.FailureNodeId}'.");
        }

        if (cue.Kind is MinigameKind.CableDiagnostics or MinigameKind.RingLock or MinigameKind.LogScan)
        {
            if (string.IsNullOrWhiteSpace(cue.PartialNodeId))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": мини-игра {cue.Kind} не задаёт partialNodeId.");

            if (!nodeIds.Contains(cue.PartialNodeId))
                throw new ScenarioValidationException(
                    Where(scene, node, null) +
                    $": partialNodeId мини-игры ссылается на несуществующий в этой сцене узел '{cue.PartialNodeId}'.");
        }

        if (cue.Kind == MinigameKind.StealthGrid)
            ValidateStealthGrid(scene, node, cue.StealthGrid);

        if (cue.Kind == MinigameKind.CodeAssembly)
            ValidateCodeAssembly(scene, node, cue.CodeAssembly);

        if (cue.Kind == MinigameKind.CableDiagnostics)
            ValidateCableDiagnostics(scene, node, cue.CableDiagnostics);

        if (cue.Kind == MinigameKind.RingLock)
            ValidateRingLock(scene, node, cue.RingLock);

        if (cue.Kind == MinigameKind.LogScan)
            ValidateLogScan(scene, node, cue.LogScan);

        if (cue.IsEpisode3D)
            ValidateEpisode3D(scene, node, cue, nodeIds);

        if (cue.Kind == MinigameKind.DossierAssembly)
            ValidateDossierAssembly(scene, node, cue.DossierAssembly);

        if (cue.Kind == MinigameKind.BirthFloorComparison && node.Choices.Count > 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": сравнение этажей должно продолжаться без сюжетного выбора на том же узле.");

        if (cue.Kind == MinigameKind.ArchiveRecordVerification && node.Choices.Count > 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": проверка архивной записи должна продолжаться без сюжетного выбора на том же узле.");

        if ((cue.Kind is MinigameKind.MarketWitnessRoute or MinigameKind.MarketCardGame) && node.Choices.Count > 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": мини-игра Ярмарки должна продолжаться без сюжетного выбора на том же узле.");
    }

    private void ValidateDossierAssembly(SceneDefinition scene, DialogueNode node, DossierAssemblyLayout? layout)
    {
        if (layout is null)
            throw new ScenarioValidationException(Where(scene, node, null) + ": мини-игра DossierAssembly не задаёт dossierAssembly.");
        if (layout.Dossiers.Count < 1 || layout.Fragments.Count < 2)
            throw new ScenarioValidationException(Where(scene, node, null) + ": dossierAssembly требует хотя бы одно досье и два фрагмента.");
        foreach (var f in layout.Fragments)
        {
            if (f.Dossier < 0 || f.Dossier >= layout.Dossiers.Count || f.Slot < 0 ||
                f.Slot >= layout.Dossiers[f.Dossier].SlotLabels.Count || string.IsNullOrWhiteSpace(f.Text))
                throw new ScenarioValidationException(Where(scene, node, null) + $": некорректный фрагмент досье '{f.Text}'.");
        }
        if (layout.Fragments.GroupBy(f => (f.Dossier, f.Slot)).Any(g => g.Count() > 1))
            throw new ScenarioValidationException(Where(scene, node, null) + ": два фрагмента претендуют на одно место в досье.");
        foreach (var id in new[] { layout.SuccessChoiceId, layout.FailureChoiceId })
        {
            if (node.Choices.All(c => c.ChoiceId != id))
                throw new ScenarioValidationException(Where(scene, node, null) + $": dossierAssembly ссылается на выбор '{id}', которого нет в узле.");
        }
        foreach (var e in layout.SuccessEffects.Concat(layout.FailureEffects))
        {
            if (!GameSessionController.IsAllowedMinigameOutcomeEffect(e))
                throw new ScenarioValidationException(Where(scene, node, null) + $": недопустимый эффект исхода {e.Type} '{e.Key}'.");
        }
    }

    /// <summary>3D-эпизоды (Главы 2А и 6): маршруты обязаны ссылаться на реальные выборы этого же
    /// узла (физический маршрут = обычный SelectChoice), исходы — только setFlag и addStat ±10, а
    /// узлы исходов — существовать в этой же сцене. Иначе эпизод отыграл бы, а сюжет не смог бы
    /// продолжиться — ошибка ловится при загрузке, а не после минуты погони.</summary>
    private void ValidateEpisode3D(SceneDefinition scene, DialogueNode node, MinigameCue cue, HashSet<string> nodeIds)
    {
        var layout = cue.Episode3D ?? throw new ScenarioValidationException(
            Where(scene, node, null) + $": мини-игра {cue.Kind} не задаёт episode3D.");

        string[] requiredRoutes = cue.Kind switch
        {
            MinigameKind.MarketStealth3D => new[] { "detour", "direct" },
            MinigameKind.ShaftsChase3D => new[] { "residential", "ventilation" },
            _ => Array.Empty<string>()
        };

        foreach (var route in requiredRoutes)
        {
            if (!layout.RouteChoiceIds.TryGetValue(route, out var choiceId) || string.IsNullOrWhiteSpace(choiceId))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": episode3D.routeChoiceIds не задаёт маршрут '{route}'.");

            if (node.Choices.All(c => c.ChoiceId != choiceId))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": маршрут '{route}' ссылается на выбор '{choiceId}', которого нет в этом узле.");
        }

        string[] requiredOutcomes = cue.Kind switch
        {
            MinigameKind.MarketStealth3D => new[] { "CLEAN", "DETECTED" },
            MinigameKind.ShaftsChase3D => new[] { "PERFECT", "NORMAL", "ROUGH" },
            _ => new[] { "CLEAN", "SPOTTED" }
        };

        if (requiredRoutes.Length == 0 && node.Choices.Count > 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + $": эпизод {cue.Kind} без маршрутов должен стоять на узле без выборов (сюжет продолжается по nextNodeId/nextSceneId).");

        foreach (var outcome in requiredOutcomes)
        {
            if (!layout.OutcomeEffects.ContainsKey(outcome))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": episode3D.outcomeEffects не задаёт исход '{outcome}'.");
        }

        foreach (var (outcome, effects) in layout.OutcomeEffects)
        {
            foreach (var effect in effects)
            {
                if (!GameSessionController.IsAllowedMinigameOutcomeEffect(effect))
                    throw new ScenarioValidationException(
                        Where(scene, node, null) +
                        $": исход '{outcome}' содержит недопустимый эффект {effect.Type} '{effect.Key}' (разрешены setFlag и addStat в пределах ±{GameSessionController.MaxMinigameStatDelta}).");
            }
        }

        foreach (var (outcome, outcomeNodeId) in layout.OutcomeNodeIds)
        {
            if (!nodeIds.Contains(outcomeNodeId))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": узел исхода '{outcome}' ('{outcomeNodeId}') не найден в этой сцене.");
        }

        if (layout.CommitRouteOnEntry && layout.OutcomeNodeIds.Count > 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) +
                ": episode3D с commitRouteOnEntry не может задавать outcomeNodeIds — после выбора маршрута узел уже сменился.");
    }

    private void ValidateRingLock(SceneDefinition scene, DialogueNode node, RingLockLayout? layout)
    {
        if (layout is null)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": мини-игра RingLock не задаёт ringLock.");

        if (layout.OuterTabCount < 4 || layout.InnerTabCount < 4)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock.outerTabCount/innerTabCount должны быть не меньше 4.");

        if (layout.RiskOuterSpeedDegPerSec <= 0 || layout.WaitOuterSpeedDegPerSec <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock: скорости вращения должны быть положительными.");

        if (layout.RiskToleranceDeg <= 0 || layout.WaitToleranceDeg <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock: допуски совмещения должны быть положительными.");

        if (layout.InnerRotateSpeedDegPerSec <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock.innerRotateSpeedDegPerSec должен быть положительным.");

        if (layout.RequiredStreak <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock.requiredStreak должен быть положительным.");

        if (layout.MaxErrorsBeforeFailure <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock.maxErrorsBeforeFailure должен быть положительным.");

        if (layout.MaxSessionSeconds <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock.maxSessionSeconds должен быть положительным.");

        if (layout.TargetHoldSeconds <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": ringLock.targetHoldSeconds должен быть положительным.");
    }

    private void ValidateLogScan(SceneDefinition scene, DialogueNode node, LogScanLayout? layout)
    {
        if (layout is null)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": мини-игра LogScan не задаёт logScan.");

        if (string.IsNullOrWhiteSpace(layout.TargetLine))
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": logScan.targetLine не задан.");

        if (layout.FillerLines.Count == 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": logScan не задаёт ни одной строки-шума (fillerLines).");

        if (layout.LinesBeforeFirstWindow < 0 || layout.LinesBetweenWindows < 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": logScan.linesBeforeFirstWindow/linesBetweenWindows не могут быть отрицательными.");

        if (layout.LineIntervalSeconds <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": logScan.lineIntervalSeconds должен быть положительным.");

        if (layout.VisibleLineCount <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": logScan.visibleLineCount должен быть положительным.");
    }

    private void ValidateCableDiagnostics(SceneDefinition scene, DialogueNode node, CableDiagnosticsLayout? layout)
    {
        if (layout is null)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": мини-игра CableDiagnostics не задаёт cableDiagnostics.");

        if (layout.GreenMin < 0 || layout.GreenMax > 1 || layout.GreenMin >= layout.GreenMax)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": cableDiagnostics.greenMin/greenMax должны задавать корректный диапазон в [0,1].");

        if (layout.StartTension < 0 || layout.StartTension > 1)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": cableDiagnostics.startTension должен быть в [0,1].");

        if (layout.RequiredStableSeconds <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": cableDiagnostics.requiredStableSeconds должен быть положительным.");

        if (layout.MaxSessionSeconds < layout.RequiredStableSeconds)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": cableDiagnostics.maxSessionSeconds должен быть не меньше requiredStableSeconds.");

        if (layout.PartialThresholdFraction < 0 || layout.PartialThresholdFraction > 1)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": cableDiagnostics.partialThresholdFraction должен быть в [0,1].");
    }

    private void ValidateCodeAssembly(SceneDefinition scene, DialogueNode node, CodeAssemblyLayout? layout)
    {
        if (layout is null)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": мини-игра CodeAssembly не задаёт codeAssembly.");

        if (layout.Pairs.Count < 2)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": codeAssembly должен задавать минимум 2 пары фрагмент/слот.");

        foreach (var pair in layout.Pairs)
            if (string.IsNullOrWhiteSpace(pair.CleanText) || string.IsNullOrWhiteSpace(pair.CorruptedHint))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + ": codeAssembly: пара без cleanText/corruptedHint.");

        if (layout.MaxErrors <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": codeAssembly.maxErrors должен быть положительным.");

        if (layout.TimeLimitSeconds <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": codeAssembly.timeLimitSeconds должен быть положительным.");

        // ложный фрагмент не может совпадать с настоящим — иначе «ошибка» была бы верным ответом
        foreach (var decoy in layout.Decoys)
            if (string.IsNullOrWhiteSpace(decoy) || layout.Pairs.Any(p => p.CleanText == decoy))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": codeAssembly.decoys: ложный фрагмент '{decoy}' пуст или совпадает с настоящим.");

        if (layout.DecaySeconds < 0 || layout.ErrorTimePenalty < 0 || layout.ShuffleSeconds < 0 ||
            layout.MaxDecayFraction is < 0 or > 1)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": codeAssembly: отрицательные параметры распада/штрафа/перемешивания.");
    }

    private void ValidateStealthGrid(SceneDefinition scene, DialogueNode node, StealthGridLayout? layout)
    {
        if (layout is null)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": мини-игра StealthGrid не задаёт stealthGrid.");

        if (layout.Rows <= 0 || layout.Cols <= 0)
            throw new ScenarioValidationException(
                Where(scene, node, null) + ": stealthGrid должен задавать положительные rows/cols.");

        bool InBounds(GridCell cell) => cell.Row >= 0 && cell.Row < layout.Rows && cell.Col >= 0 && cell.Col < layout.Cols;

        if (!InBounds(layout.Start))
            throw new ScenarioValidationException(
                Where(scene, node, null) + $": stealthGrid.start ({layout.Start.Row},{layout.Start.Col}) вне сетки {layout.Rows}x{layout.Cols}.");

        if (!InBounds(layout.Exit))
            throw new ScenarioValidationException(
                Where(scene, node, null) + $": stealthGrid.exit ({layout.Exit.Row},{layout.Exit.Col}) вне сетки {layout.Rows}x{layout.Cols}.");

        foreach (var cover in layout.CoverCells)
            if (!InBounds(cover))
                throw new ScenarioValidationException(
                    Where(scene, node, null) + $": stealthGrid: укрытие ({cover.Row},{cover.Col}) вне сетки {layout.Rows}x{layout.Cols}.");

        if (layout.Drones.Count == 0)
            throw new ScenarioValidationException(Where(scene, node, null) + ": stealthGrid не задаёт ни одного дрона.");

        foreach (var drone in layout.Drones)
        {
            if (drone.Waypoints.Count < 2)
                throw new ScenarioValidationException(
                    Where(scene, node, null) + ": stealthGrid: маршрут дрона должен содержать минимум 2 точки.");

            foreach (var waypoint in drone.Waypoints)
                if (!InBounds(waypoint))
                    throw new ScenarioValidationException(
                        Where(scene, node, null) + $": stealthGrid: точка маршрута дрона ({waypoint.Row},{waypoint.Col}) вне сетки {layout.Rows}x{layout.Cols}.");
        }
    }

    private void ValidateChoice(
        SceneDefinition scene, DialogueNode node, ChoiceDefinition choice,
        HashSet<string> choiceIds, HashSet<string> nodeIds, HashSet<string> sceneIds)
    {
        if (string.IsNullOrWhiteSpace(choice.ChoiceId))
            throw new ScenarioValidationException(Where(scene, node, null) + ": выбор с пустым идентификатором.");

        if (!choiceIds.Add(choice.ChoiceId))
            throw new ScenarioValidationException(
                Where(scene, node, null) + $": дублирующийся идентификатор выбора '{choice.ChoiceId}'.");

        if (string.IsNullOrWhiteSpace(choice.Text))
            throw new ScenarioValidationException(Where(scene, node, choice.ChoiceId) + ": отсутствует текст варианта.");

        var destinationCount =
            CountNonEmpty(choice.NextNodeId) + CountNonEmpty(choice.NextSceneId) + CountNonEmpty(choice.EndingId);

        if (destinationCount != 1)
            throw new ScenarioValidationException(
                Where(scene, node, choice.ChoiceId) +
                $": должен вести ровно в одно место (узел, сцену или концовку), а указано направлений: {destinationCount}.");

        if (choice.NextNodeId is not null && !nodeIds.Contains(choice.NextNodeId))
            throw new ScenarioValidationException(
                Where(scene, node, choice.ChoiceId) + $": nextNodeId ссылается на несуществующий узел '{choice.NextNodeId}'.");

        if (choice.NextSceneId is not null && !sceneIds.Contains(choice.NextSceneId))
            throw new ScenarioValidationException(
                Where(scene, node, choice.ChoiceId) + $": nextSceneId ссылается на несуществующую сцену '{choice.NextSceneId}'.");

        ValidateCondition(scene, node, choice.ChoiceId, choice.Condition);

        foreach (var effect in choice.Effects)
            ValidateEffect(scene, node, choice.ChoiceId, effect);
    }

    private void ValidateCondition(SceneDefinition scene, DialogueNode node, string? choiceId, ConditionNode? condition)
    {
        switch (condition)
        {
            case null:
                return;

            case FlagEqualsCondition flagCondition when string.IsNullOrWhiteSpace(flagCondition.FlagKey):
                throw new ScenarioValidationException(Where(scene, node, choiceId) + ": условие проверки флага без ключа.");

            case AndCondition { Children.Count: 0 }:
                throw new ScenarioValidationException(Where(scene, node, choiceId) + ": условие 'and' без дочерних условий.");

            case OrCondition { Children.Count: 0 }:
                throw new ScenarioValidationException(Where(scene, node, choiceId) + ": условие 'or' без дочерних условий.");

            case AndCondition andCondition:
                foreach (var child in andCondition.Children)
                    ValidateCondition(scene, node, choiceId, child);
                break;

            case OrCondition orCondition:
                foreach (var child in orCondition.Children)
                    ValidateCondition(scene, node, choiceId, child);
                break;

            case NotCondition notCondition:
                ValidateCondition(scene, node, choiceId, notCondition.Child);
                break;
        }
    }

    private void ValidateEffect(SceneDefinition scene, DialogueNode node, string? choiceId, Effect effect)
    {
        if (string.IsNullOrWhiteSpace(effect.Key))
            throw new ScenarioValidationException(Where(scene, node, choiceId) + ": эффект без ключа.");

        switch (effect.Type)
        {
            case EffectType.SetFlag when effect.FlagValue is null:
                throw new ScenarioValidationException(
                    Where(scene, node, choiceId) + $": эффект setFlag '{effect.Key}' не содержит значения.");

            case EffectType.AddStat or EffectType.SetStat when effect.NumericValue is null:
                throw new ScenarioValidationException(
                    Where(scene, node, choiceId) + $": эффект '{effect.Type}' '{effect.Key}' не содержит числового значения.");

            case EffectType.AddStat or EffectType.SetStat when !Enum.TryParse<StatId>(effect.Key, true, out _):
                throw new ScenarioValidationException(
                    Where(scene, node, choiceId) + $": '{effect.Key}' не является известной шкалой (K/L/R/H).");
        }
    }

    private static int CountNonEmpty(string? value) => string.IsNullOrEmpty(value) ? 0 : 1;

    private static string Where(SceneDefinition scene, DialogueNode node, string? choiceId) =>
        choiceId is null
            ? $"Сцена '{scene.SceneId}', узел '{node.NodeId}'"
            : $"Сцена '{scene.SceneId}', узел '{node.NodeId}', выбор '{choiceId}'";
}
