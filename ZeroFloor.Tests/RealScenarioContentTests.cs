using ZeroFloor.Engine;
using ZeroFloor.Models;
using ZeroFloor.Services;

namespace ZeroFloor.Tests;

/// <summary>
/// Сквозная проверка реального сценарного контента (Data/Scenario/*.json). Главы 1–9 перенесены
/// дословно из Zero_Floor_Full_Script.docx (редакция 2.2) — сцены CH0x_SCyy по каталогу раздела 0
/// документа; Главы 10–12 (+ концовки) остаются структурным переносом из Story Bible/Flowchart
/// (ждут своего прохода с полным текстом). Этот файл проверяет загрузку и валидацию всего
/// репозитория через настоящие ScenarioRepository/ScenarioValidator/GameSessionController, и
/// полное прохождение по четырём числовым примерам из Story Bible (раздел 9.4) — K/L/R/H и флаги
/// после Главы 5 здесь идентичны прежним (эффекты выборов не менялись, только их форма в данных).
/// Главы 6–9 теперь несут полный текст: Глава 6 (погоня по шахтам), Глава 7 (диалоговое дерево
/// у Ани, медальон), Глава 8 (архив Смотрителей + файл отца), Глава 9 (должник — три пути
/// спасения Дэна, твист №6, матрица исходов для Дэна по Приложению 2.3 документа). Полный текст
/// Главы 9 ввёл несколько новых узлов-гейтов (dan_flagged_by_leak на входе, dan_status на
/// выходе) — вспомогательные методы ниже учитывают их явно.
/// </summary>
public class RealScenarioContentTests
{
    internal static string ScenarioDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Data", "Scenario");

    internal static GameSessionController CreateController(GameState? state = null)
    {
        var repository = ScenarioRepository.LoadFromDirectory(ScenarioDirectory);
        var sceneManager = new SceneManager(repository);
        var effectApplier = new EffectApplier();
        var dialogueManager = new DialogueManager(sceneManager, effectApplier);
        var conditionEvaluator = new ConditionEvaluator();
        var choiceProcessor = new ChoiceProcessor(conditionEvaluator, effectApplier);
        var endingManager = new EndingManager(conditionEvaluator);

        return new GameSessionController(dialogueManager, choiceProcessor, endingManager, state ?? new GameState());
    }

    /// <summary>
    /// Автоматически проходит все узлы без выбора (нарративные реплики, в т.ч. переходы между
    /// под-сценами одной главы), пока не окажется на узле с хотя бы одним вариантом выбора —
    /// или пока концовка не будет уже зафиксирована. Так тест не завязан на точное число
    /// промежуточных нарративных узлов внутри главы, только на реальные точки выбора.
    /// </summary>
    internal static void AdvanceToChoice(GameSessionController controller)
    {
        while (!controller.HasEnded && controller.CurrentNode.Choices.Count == 0)
            controller.Advance();
    }

    internal static void SelectAndAdvance(GameSessionController controller, string choiceId)
    {
        AdvanceToChoice(controller);
        controller.SelectChoice(choiceId);
    }

    [Fact]
    public void LoadFromDirectory_AllRealChapters_PassesValidation()
    {
        var repository = ScenarioRepository.LoadFromDirectory(ScenarioDirectory);

        string[] expectedSceneIds =
        {
            "CH01_SC01", "CH01_SC02", "CH01_SC03",
            "CH02_SC01", "CH02_SC02", "CH02_SC03",
            "CH03_SC01", "CH03_SC02", "CH03_SC03", "CH03_SC04",
            "CH04_SC01",
            "CH05_SC01", "CH05_SC02", "CH05_SC03", "CH05_SC05", "CH05_SC_SIDE02",
            "CH06_SHAFTS_CHASE", "CH06_SHAFTS_CHASE_02",
            "CH07_MEDALLION", "CH07_MEDALLION_02", "CH07_MEDALLION_03", "CH07_MEDALLION_04",
            "CH08_WATCHERS_ARCHIVE", "CH08_WATCHERS_ARCHIVE_02", "CH08_WATCHERS_ARCHIVE_03",
            "CH09_DEBTOR_GAME", "CH09_DEBTOR",
            "CH10_SHADOW_COUNCIL",
            "CH11A_CROWN_RECEPTION", "CH11A_CROWN_RECEPTION_02",
            "CH11B_CORE_COLLAPSE", "CH11B_CORE_COLLAPSE_02",
            "CH11C_WATCHER_POST",
            "CH11D_CORE_APPROACH", "CH11D_ANI_BIOMETRY", "CH11D_THREE_TERMINALS",
            "CH12_FINALE_A", "CH12_FINALE_A_EPILOGUE",
            "CH12_FINALE_B", "CH12_FINALE_B_EPILOGUE",
            "CH12_FINALE_C", "CH12_FINALE_C_EPILOGUE",
            "CH12_FINALE_D", "CH12_FINALE_D_EPILOGUE"
        };

        foreach (var sceneId in expectedSceneIds)
            Assert.NotNull(repository.GetScene(sceneId));
    }

    /// <summary>
    /// Проходит Главы 1–5 одним и тем же стилем выборов, параметризованным по тесту (только
    /// теми развилками, что реально меняют K/L/R/H/флаги — косметические развилки вроде тона
    /// реплики Макса в Главе 1 или маршрута обхода дрона захардкожены внутри, раз они ни на что
    /// не влияют), и останавливается сразу по входу в CH06_SHAFTS_CHASE.
    /// </summary>
    private static void PlayChapters1Through5(
        GameSessionController controller,
        string ch1Choice,
        string ch2DanChoice,
        string ch3ShardChoice,
        string ch4JoinChoice,
        string ch5DenyOrPartial,
        bool haggleTwice = false)
    {
        controller.Start("CH01_SC01");
        SelectAndAdvance(controller, ch1Choice);

        // Сообщение от тётки Клавдии (Глава 2) — автоматический гейт по H, не реальный выбор игрока.
        var klavdiaGate = ch1Choice == "CH01_SC03_C001_A" ? "CH02_SC01_C_KLAVDIA_B" : "CH02_SC01_C_KLAVDIA_A";
        SelectAndAdvance(controller, klavdiaGate);
        SelectAndAdvance(controller, "CH02_SC01_C001_A"); // обойти дрона — флейвор, без эффектов
        if (haggleTwice)
        {
            SelectAndAdvance(controller, "CH02_SC02_C001_B");
            SelectAndAdvance(controller, "CH02_SC02_C002_A");
            SelectAndAdvance(controller, "CH02_SC02_C003T_PAY");
        }
        else
        {
            SelectAndAdvance(controller, "CH02_SC02_C001_A");
            SelectAndAdvance(controller, "CH02_SC02_C003D_PAY");
        }
        SelectAndAdvance(controller, ch2DanChoice);

        // Узел CH03_SC02 (взлом панели) больше не задаёт текстовый выбор "быстро/аккуратно" —
        // это теперь мини-игра "Взлом лифтового узла" (презентационный слой, движок её не
        // видит), AdvanceToChoice проходит сквозь неё как через обычную реплику.
        SelectAndAdvance(controller, ch3ShardChoice);

        SelectAndAdvance(controller, "CH04_SC01_C001_A"); // «Кто ты вообще такой?»
        SelectAndAdvance(controller, "CH04_SC01_C002_A"); // «И что ты написал?»
        SelectAndAdvance(controller, "CH04_SC01_C003_B"); // промолчать (твист-намёк, без эффектов)
        SelectAndAdvance(controller, "CH04_SC01_C004_A"); // «Что будет, если я соглашусь сотрудничать?»
        SelectAndAdvance(controller, ch4JoinChoice);

        SelectAndAdvance(controller, "CH05_SC01_C001_B"); // сидеть молча — флейвор, без эффектов
        SelectAndAdvance(controller, "CH05_SC02_C001_B"); // не рисковать запиской (cellmate_helped=false)
        // Допрос: три логичных ответа подряд (suspicion доходит до 0 — CH05_SC05 не появляется
        // ни при «отрицать», ни при «частично сотрудничать», см. ПРИЛОЖЕНИЕ 2.2 документа).
        SelectAndAdvance(controller, "CH05_SC03_C001_A");
        SelectAndAdvance(controller, "CH05_SC03_C002_A1");
        SelectAndAdvance(controller, "CH05_SC03_C003_A1");
        SelectAndAdvance(controller, ch5DenyOrPartial);

        AdvanceToChoice(controller);
    }

    [Fact]
    public void Example1_CooperationWithRoots_UnlocksCollapseBranch()
    {
        // Story Bible, раздел 9.4, Пример 1 — K должно достичь 75 к Главе 10 (ветка B доступна).
        var controller = CreateController();

        PlayChapters1Through5(
            controller,
            ch1Choice: "CH01_SC03_C001_A", // взломать сейчас, +5H -> 65
            ch2DanChoice: "CH02_SC03_C001_A", // буду осторожнее
            ch3ShardChoice: "CH03_SC04_C001_A", // извлечь шард
            ch4JoinChoice: "CH04_SC01_C005_A", // открыто согласиться, +15K/-5L -> K65 L45
            ch5DenyOrPartial: "CH05_SC03_C004_DENY_0"); // -5L -> L40

        Assert.Equal(65, controller.State.GetStat(StatId.K));
        Assert.Equal(40, controller.State.GetStat(StatId.L));
        Assert.Equal(70, controller.State.GetStat(StatId.H));
        Assert.Equal("CH06_SHAFTS_CHASE", controller.State.CurrentSceneId);

        // Главы 6–9 (ещё не перенесены дословно — используют прежнюю структурную версию):
        // вентиляция (+10H), дать время (+5H), сохранить тайну Виктора, не трогать файл отца,
        // не играть в подпольную игру, силовое спасение Дэна (K>=30 всегда проходимо).
        SelectAndAdvance(controller, "C06_ROUTE_VENTILATION");
        SelectAndAdvance(controller, "C06_RESULT_NORMAL_GATE"); // служебный гейт исхода 3D-погони (mg_ch6_chase_result не задан → обычный исход)
        AdvanceToChoice(controller);

        SelectAndAdvance(controller, "C07_TREE_A");
        SelectAndAdvance(controller, "C07_NOTE_SHAKING_HANDS");
        SelectAndAdvance(controller, "C07_NOTE_MEDALLION");
        SelectAndAdvance(controller, "C07_GIVE_HER_TIME");

        SelectAndAdvance(controller, "C08_MATCH_FRAGMENTS");
        SelectAndAdvance(controller, "C08_KEEP_SECRET");
        SelectAndAdvance(controller, "C08_LEAVE_FILE_ALONE");

        SelectAndAdvance(controller, "C09_GAME_SKIP");
        SelectAndAdvance(controller, "C09_TOOL_LOST_GATE"); // реплика Гоши про гаечный ключ (tool_lost из Выбора 6)
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE"); // dan_flagged_by_leak=false
        SelectAndAdvance(controller, "C09_PRESS_WITNESS");
        SelectAndAdvance(controller, "C09_PATH_FORCE"); // +10K/-10L -> K75 L30
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_HEEDED_GATE");
        AdvanceToChoice(controller);

        Assert.Equal("CH10_SHADOW_COUNCIL", controller.State.CurrentSceneId);
        Assert.Equal(75, controller.State.GetStat(StatId.K));
        Assert.Equal(30, controller.State.GetStat(StatId.L));
        Assert.Equal(85, controller.State.GetStat(StatId.H));

        var branches = controller.GetAvailableEndingBranches();
        Assert.Contains("A", branches);
        Assert.Contains("B", branches);
        Assert.DoesNotContain("C", branches);
        Assert.DoesNotContain("D", branches);

        SelectAndAdvance(controller, "C10_BRANCH_B_REFUSE_THREATEN");
        Assert.Equal("CH11B_CORE_COLLAPSE", controller.State.CurrentSceneId);

        SelectAndAdvance(controller, "C11B_WAIT_FOR_EVACUATION");
        AdvanceToChoice(controller); // отцовское прощание + кульминация главы 12.B — чистая narration
        Assert.Equal("CH12_FINALE_B_EPILOGUE", controller.State.CurrentSceneId);

        DrainToEnding(controller);
        Assert.True(controller.HasEnded);
        Assert.Equal("END_01_COLLAPSE", controller.State.CurrentEndingId);
    }

    [Fact]
    public void Example3_AlwaysAvailableEscapeBranch_IsReachableEvenForSelfishPlaythrough()
    {
        var controller = CreateController();

        PlayChapters1Through5(
            controller,
            ch1Choice: "CH01_SC03_C001_B", // подождать очереди, -5H -> 55
            ch2DanChoice: "CH02_SC03_C001_B", // отмахнуться шуткой, dan_warned_heeded=false
            ch3ShardChoice: "CH03_SC04_C001_B", // уйти чисто, shard_obtained=false
            ch4JoinChoice: "CH04_SC01_C005_B", // держать дистанцию, +5K -> K55
            ch5DenyOrPartial: "CH05_SC03_C004_PARTIAL_0", // +20L/-5H -> L70 H55
            haggleTwice: true); // Гоша теряет доверие после двух попыток сбить цену

        Assert.Equal(55, controller.State.GetStat(StatId.K));
        Assert.Equal(70, controller.State.GetStat(StatId.L));
        Assert.Equal(55, controller.State.GetStat(StatId.H));
        Assert.False(controller.State.GetFlag("shard_obtained")!.Value.BooleanValue);
        Assert.False(controller.State.GetFlag("gosha_trust")!.Value.BooleanValue);

        SelectAndAdvance(controller, "C06_ROUTE_RESIDENTIAL"); // -10H -> H45
        SelectAndAdvance(controller, "C06_RESULT_NORMAL_GATE"); // служебный гейт исхода 3D-погони (mg_ch6_chase_result не задан → обычный исход)
        AdvanceToChoice(controller);

        SelectAndAdvance(controller, "C07_TREE_A");
        SelectAndAdvance(controller, "C07_IGNORE_SHAKING_HANDS");
        SelectAndAdvance(controller, "C07_IGNORE_MEDALLION");
        SelectAndAdvance(controller, "C07_DEMAND_TRUTH_NOW"); // -5H -> H40

        SelectAndAdvance(controller, "C08_GUESS_RANDOMLY");
        SelectAndAdvance(controller, "C08_KEEP_SECRET");
        SelectAndAdvance(controller, "C08_LEAVE_FILE_ALONE");

        SelectAndAdvance(controller, "C09_GAME_SKIP");
        SelectAndAdvance(controller, "C09_TOOL_KEPT_GATE"); // реплика Гоши про гаечный ключ (tool_lost из Выбора 6)
        SelectAndAdvance(controller, "C09_LEAK_TRUE_GATE"); // dan_flagged_by_leak=true

        // Единственный реально доступный путь: силовой (нет шарда, gosha_trust=false).
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        AdvanceToChoice(controller);
        var availablePaths = controller.GetAvailableChoices();
        Assert.Single(availablePaths);
        Assert.Equal("C09_PATH_FORCE", availablePaths[0].ChoiceId);

        controller.SelectChoice("C09_PATH_FORCE"); // +10K/-10L -> K65 L60
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        controller.SelectChoice("C09_DAN_FATE_IGNORED_GATE");
        // Худшее сочетание по Приложению 2.3 документа: силовой путь, предупреждение Дэна
        // (Глава 2) не услышано, ни одна альтернатива (шард/отсрочка) не была доступна — Дэн погибает.
        controller.SelectChoice("C09_FORCE_NO_ALT_GATE");

        AdvanceToChoice(controller);

        Assert.Equal("CH10_SHADOW_COUNCIL", controller.State.CurrentSceneId);
        Assert.Equal("dead", controller.State.GetFlag("dan_status")!.Value.StringValue);
        Assert.Equal(65, controller.State.GetStat(StatId.K));
        Assert.Equal(60, controller.State.GetStat(StatId.L));
        Assert.Equal(40, controller.State.GetStat(StatId.H));

        var branches = controller.GetAvailableEndingBranches();
        Assert.Contains("A", branches);
        Assert.Contains("B", branches);
        Assert.DoesNotContain("C", branches);
        Assert.DoesNotContain("D", branches);

        SelectAndAdvance(controller, "C10_BRANCH_A_ACCEPT_DEAL");
        Assert.Equal("CH11A_CROWN_RECEPTION", controller.State.CurrentSceneId);
    }

    [Fact]
    public void Example4_FatherFileFound_UnlocksSuccessorBranch_RegardlessOfStats()
    {
        var controller = CreateController();
        var state = controller.State;

        PlayChapters1Through5(
            controller,
            ch1Choice: "CH01_SC03_C001_A",
            ch2DanChoice: "CH02_SC03_C001_A",
            ch3ShardChoice: "CH03_SC04_C001_A",
            ch4JoinChoice: "CH04_SC01_C005_B",
            ch5DenyOrPartial: "CH05_SC03_C004_DENY_0");

        SelectAndAdvance(controller, "C06_ROUTE_VENTILATION");
        SelectAndAdvance(controller, "C06_RESULT_NORMAL_GATE"); // служебный гейт исхода 3D-погони (mg_ch6_chase_result не задан → обычный исход)
        AdvanceToChoice(controller);

        SelectAndAdvance(controller, "C07_TREE_A");
        SelectAndAdvance(controller, "C07_NOTE_SHAKING_HANDS");
        SelectAndAdvance(controller, "C07_NOTE_MEDALLION");
        SelectAndAdvance(controller, "C07_GIVE_HER_TIME");

        SelectAndAdvance(controller, "C08_MATCH_FRAGMENTS");
        SelectAndAdvance(controller, "C08_EXPOSE_VIKTOR_PUBLICLY");
        SelectAndAdvance(controller, "C08_OPEN_FATHER_FILE");

        SelectAndAdvance(controller, "C09_GAME_SKIP");
        SelectAndAdvance(controller, "C09_TOOL_LOST_GATE"); // реплика Гоши про гаечный ключ (tool_lost из Выбора 6)
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE"); // dan_flagged_by_leak=false
        SelectAndAdvance(controller, "C09_PRESS_WITNESS");
        SelectAndAdvance(controller, "C09_PATH_FORCE");
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_HEEDED_GATE");

        Assert.True(state.GetFlag("father_file_found")!.Value.BooleanValue);

        var branches = controller.GetAvailableEndingBranches();
        Assert.Contains("A", branches);
        Assert.Contains("C", branches); // father_file_found=true — независимо от K/L

        SelectAndAdvance(controller, "C10_BRANCH_C_DEMAND_SUCCESSION");
        Assert.Equal("CH11C_WATCHER_POST", controller.State.CurrentSceneId);
        Assert.Equal("C", state.GetFlag("ending_branch")!.Value.StringValue);
    }

    [Fact]
    public void Example2_GentlePlaythrough_UnlocksHiddenBalanceBranch()
    {
        var controller = CreateController();
        var state = controller.State;

        PlayChapters1Through5(
            controller,
            ch1Choice: "CH01_SC03_C001_A", // +5H -> 65
            ch2DanChoice: "CH02_SC03_C001_A",
            ch3ShardChoice: "CH03_SC04_C001_B", // уйти чисто
            ch4JoinChoice: "CH04_SC01_C005_B", // держать дистанцию, +5K
            ch5DenyOrPartial: "CH05_SC03_C004_DENY_0");

        SelectAndAdvance(controller, "C06_ROUTE_VENTILATION"); // +10H -> H85 (clamp 100 не достигается)
        SelectAndAdvance(controller, "C06_RESULT_NORMAL_GATE"); // служебный гейт исхода 3D-погони (mg_ch6_chase_result не задан → обычный исход)
        AdvanceToChoice(controller);

        SelectAndAdvance(controller, "C07_TREE_A");
        SelectAndAdvance(controller, "C07_NOTE_SHAKING_HANDS");
        SelectAndAdvance(controller, "C07_NOTE_MEDALLION");
        SelectAndAdvance(controller, "C07_GIVE_HER_TIME"); // +5H

        SelectAndAdvance(controller, "C08_MATCH_FRAGMENTS");
        SelectAndAdvance(controller, "C08_KEEP_SECRET");
        SelectAndAdvance(controller, "C08_OPEN_FATHER_FILE"); // +5H, father_file_found=true

        SelectAndAdvance(controller, "C09_GAME_SKIP");
        SelectAndAdvance(controller, "C09_TOOL_LOST_GATE"); // реплика Гоши про гаечный ключ (tool_lost из Выбора 6)
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE"); // dan_flagged_by_leak=false
        SelectAndAdvance(controller, "C09_PRESS_WITNESS");
        SelectAndAdvance(controller, "C09_PATH_FORCE");
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_HEEDED_GATE");

        Assert.True(state.GetStat(StatId.H) >= 70);
        Assert.True(state.GetFlag("father_file_found")!.Value.BooleanValue);
        // sonya_exposed никогда явно не выставлялся на этом пути (suspicion не достиг порога
        // показа CH05_SC05) — по умолчанию (флаг отсутствует) это равносильно false.
        Assert.True(state.GetFlag("sonya_exposed") is not { BooleanValue: true });

        var branches = controller.GetAvailableEndingBranches();
        Assert.Contains("A", branches);
        Assert.Contains("C", branches);
        Assert.Contains("D", branches);

        SelectAndAdvance(controller, "C10_BRANCH_D_COUNTER_COMPROMISE");
        Assert.Equal("CH11D_CORE_APPROACH", controller.State.CurrentSceneId);

        SelectAndAdvance(controller, "C11D_FATHER_CONFIRMS_FIRST");
        Assert.Equal("CH11D_THREE_TERMINALS", controller.State.CurrentSceneId);

        DrainToEnding(controller);
        Assert.Equal("END_04_BALANCE", controller.State.CurrentEndingId);
    }

    /// <summary>Доходит до конца текущей главы (Главы 12), на каждом узле с выбором беря первый
    /// доступный вариант, пока концовка не будет зафиксирована.</summary>
    private static void DrainToEnding(GameSessionController controller)
    {
        for (var i = 0; i < 30 && !controller.HasEnded; i++)
        {
            if (controller.CurrentNode.Choices.Count == 0)
            {
                controller.Advance();
                continue;
            }

            var choices = controller.GetAvailableChoices();
            controller.SelectChoice(choices[0].ChoiceId);
        }
    }

    [Fact]
    public void AllFourChapter12Variants_ReachTheirOwnEnding()
    {
        AssertVariantReachesEnding("CH12_FINALE_A", "END_03_ESCAPE");
        AssertVariantReachesEnding("CH12_FINALE_B", "END_01_COLLAPSE");
        AssertVariantReachesEnding("CH12_FINALE_C", "END_02_SUCCESSOR");
        AssertVariantReachesEnding("CH12_FINALE_D", "END_04_BALANCE");
    }

    private static void AssertVariantReachesEnding(string startingSceneId, string expectedEndingId)
    {
        var controller = CreateController();
        controller.Start(startingSceneId);

        DrainToEnding(controller);

        Assert.True(controller.HasEnded);
        Assert.Equal(expectedEndingId, controller.State.CurrentEndingId);
    }

    [Fact]
    public void Gate9_RansomPath_RequiresShardObtained()
    {
        var controller = CreateController();
        controller.State.SetFlag("shard_obtained", FlagValue.FromBoolean(false));
        controller.State.SetFlag("gosha_trust", FlagValue.FromBoolean(false));
        controller.State.SetStat(StatId.R, 50);

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE"); // dan_flagged_by_leak unset -> default false
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        AdvanceToChoice(controller);

        var available = controller.GetAvailableChoices();

        Assert.Contains(available, c => c.ChoiceId == "C09_PATH_FORCE");
        Assert.DoesNotContain(available, c => c.ChoiceId == "C09_PATH_RANSOM");
        Assert.DoesNotContain(available, c => c.ChoiceId == "C09_PATH_TRUCE");
    }

    [Fact]
    public void Gate9_TrucePath_RequiresRAtLeast55AndGoshaTrust()
    {
        var controller = CreateController();
        controller.State.SetFlag("gosha_trust", FlagValue.FromBoolean(true));
        controller.State.SetStat(StatId.R, 55);

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE"); // dan_flagged_by_leak unset -> default false
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        AdvanceToChoice(controller);

        var available = controller.GetAvailableChoices();
        Assert.Contains(available, c => c.ChoiceId == "C09_PATH_TRUCE");
    }

    [Fact]
    public void Gate9_TrucePath_HiddenAtR54()
    {
        var controller = CreateController();
        controller.State.SetFlag("gosha_trust", FlagValue.FromBoolean(true));
        controller.State.SetStat(StatId.R, 54);

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE"); // dan_flagged_by_leak unset -> default false
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        AdvanceToChoice(controller);

        var available = controller.GetAvailableChoices();
        Assert.DoesNotContain(available, c => c.ChoiceId == "C09_PATH_TRUCE");
    }

    /// <summary>T06 (раздел I Библии): высокий R сам по себе не открывает отсрочку без gosha_trust.</summary>
    [Fact]
    public void Gate9_TrucePath_HiddenWhenGoshaTrustFalse_EvenAtHighR()
    {
        var controller = CreateController();
        controller.State.SetFlag("gosha_trust", FlagValue.FromBoolean(false));
        controller.State.SetStat(StatId.R, 80);

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE");
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        AdvanceToChoice(controller);

        var available = controller.GetAvailableChoices();
        Assert.DoesNotContain(available, c => c.ChoiceId == "C09_PATH_TRUCE");
    }

    /// <summary>
    /// Три пути спасения Дэна в Главе 9, доведённые до полного исхода (Приложение 2.3
    /// документа — матрица dan_status), не только до проверки доступности варианта.
    /// </summary>
    [Fact]
    public void Chapter9_ForcePath_Heeded_DanSurvivesWithLightWound()
    {
        var controller = CreateController();
        controller.State.SetFlag("dan_warned_heeded", FlagValue.FromBoolean(true));

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE");
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        SelectAndAdvance(controller, "C09_PATH_FORCE");
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_HEEDED_GATE");
        AdvanceToChoice(controller);

        Assert.Equal("alive", controller.State.GetFlag("dan_status")!.Value.StringValue);
        Assert.Equal("CH10_SHADOW_COUNCIL", controller.State.CurrentSceneId);
    }

    [Fact]
    public void Chapter9_ForcePath_IgnoredWithoutAnyAlternative_DanDies()
    {
        var controller = CreateController();
        controller.State.SetFlag("dan_warned_heeded", FlagValue.FromBoolean(false));
        controller.State.SetFlag("shard_obtained", FlagValue.FromBoolean(false));
        controller.State.SetFlag("gosha_trust", FlagValue.FromBoolean(false));
        controller.State.SetStat(StatId.R, 50);

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE");
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        SelectAndAdvance(controller, "C09_PATH_FORCE");
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_IGNORED_GATE");
        SelectAndAdvance(controller, "C09_FORCE_NO_ALT_GATE");
        AdvanceToChoice(controller);

        Assert.Equal("dead", controller.State.GetFlag("dan_status")!.Value.StringValue);
        Assert.Equal("CH10_SHADOW_COUNCIL", controller.State.CurrentSceneId);
    }

    [Fact]
    public void Chapter9_ForcePath_IgnoredWithAlternativeAvailable_DanSurvivesWithMediumWound()
    {
        var controller = CreateController();
        controller.State.SetFlag("dan_warned_heeded", FlagValue.FromBoolean(false));
        controller.State.SetFlag("shard_obtained", FlagValue.FromBoolean(true));

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE");
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        SelectAndAdvance(controller, "C09_PATH_FORCE"); // силовой, несмотря на доступную альтернативу (шард)
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_IGNORED_GATE");
        SelectAndAdvance(controller, "C09_FORCE_ALT_AVAILABLE_GATE");
        AdvanceToChoice(controller);

        Assert.Equal("alive", controller.State.GetFlag("dan_status")!.Value.StringValue);
        Assert.Equal("CH10_SHADOW_COUNCIL", controller.State.CurrentSceneId);
    }

    [Fact]
    public void Chapter9_RansomPath_SpendsShard_DanSurvivesUnharmed()
    {
        var controller = CreateController();
        controller.State.SetFlag("shard_obtained", FlagValue.FromBoolean(true));
        controller.State.SetFlag("dan_warned_heeded", FlagValue.FromBoolean(true));

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE");
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        AdvanceToChoice(controller);
        var available = controller.GetAvailableChoices();
        Assert.Contains(available, c => c.ChoiceId == "C09_PATH_RANSOM");

        SelectAndAdvance(controller, "C09_PATH_RANSOM"); // +5H; шард расходуется
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_HEEDED_GATE");
        AdvanceToChoice(controller);

        Assert.Equal("alive", controller.State.GetFlag("dan_status")!.Value.StringValue);
        Assert.True(controller.State.GetFlag("shard_spent")!.Value.BooleanValue);
        Assert.Equal("CH10_SHADOW_COUNCIL", controller.State.CurrentSceneId);
    }

    [Fact]
    public void Chapter9_TrucePath_NoShardSpent_DanSurvivesUnharmed()
    {
        var controller = CreateController();
        controller.State.SetFlag("gosha_trust", FlagValue.FromBoolean(true));
        controller.State.SetStat(StatId.R, 55);
        controller.State.SetFlag("dan_warned_heeded", FlagValue.FromBoolean(true));

        controller.Start("CH09_DEBTOR");
        SelectAndAdvance(controller, "C09_LEAK_FALSE_GATE");
        SelectAndAdvance(controller, "C09_ASK_GENTLY");
        AdvanceToChoice(controller);
        var available = controller.GetAvailableChoices();
        Assert.Contains(available, c => c.ChoiceId == "C09_PATH_TRUCE");

        SelectAndAdvance(controller, "C09_PATH_TRUCE"); // +5R/+5H; улика не расходуется
        SelectAndAdvance(controller, "C09_NO_CELLMATE_GATE");
        SelectAndAdvance(controller, "C09_DAN_FATE_HEEDED_GATE");
        AdvanceToChoice(controller);

        Assert.Equal("alive", controller.State.GetFlag("dan_status")!.Value.StringValue);
        // shard_obtained никогда не выставлялся на этом пути — отсутствие флага равносильно
        // false по умолчанию (см. ConditionEvaluator.EvaluateFlagEquals), улика не тронута.
        Assert.True(controller.State.GetFlag("shard_obtained") is not { BooleanValue: true });
        Assert.Equal("CH10_SHADOW_COUNCIL", controller.State.CurrentSceneId);
    }

    [Theory]
    [InlineData("CH09_DEBTOR_RESOLUTION_DEAD_02")]
    [InlineData("CH09_DEBTOR_RESOLUTION_ALIVE_06")]
    public void Chapter9_BothDanOutcomes_LeadThroughSummonsAndReceptionToWessel(string lastNode)
    {
        var controller = CreateController();
        controller.Resume("CH09_DEBTOR", lastNode);

        foreach (var expectedNode in new[]
        {
            "CH09_DEBTOR_SUMMONS_LOOKUP",
            "CH09_DEBTOR_SUMMONS_CALL",
            "CH09_DEBTOR_SUMMONS_WESSEL",
            "CH09_DEBTOR_SUMMONS_BOARD",
            "CH10_ADMIN_ARRIVAL_D001",
            "CH10_ADMIN_ARRIVAL_D002",
            "CH10_SHADOW_COUNCIL_D001"
        })
        {
            controller.Advance();
            Assert.Equal(expectedNode, controller.CurrentNode.NodeId);
        }
    }

    [Fact]
    public void Gate10_BranchB_HiddenAtK39_VisibleAtK40()
    {
        var belowThreshold = CreateController();
        belowThreshold.State.SetStat(StatId.K, 39);
        var belowBranches = belowThreshold.GetAvailableEndingBranches();
        Assert.DoesNotContain("B", belowBranches);
        Assert.Contains("A", belowBranches);

        var atThreshold = CreateController();
        atThreshold.State.SetStat(StatId.K, 40);
        var atBranches = atThreshold.GetAvailableEndingBranches();
        Assert.Contains("B", atBranches);
    }

    [Fact]
    public void Gate10_BranchD_RequiresAllThreeConditions()
    {
        var controller = CreateController();
        controller.State.SetStat(StatId.H, 70);
        controller.State.SetFlag("father_file_found", FlagValue.FromBoolean(true));
        controller.State.SetFlag("sonya_exposed", FlagValue.FromBoolean(false));

        var branches = controller.GetAvailableEndingBranches();
        Assert.Contains("D", branches);

        var exposedController = CreateController();
        exposedController.State.SetStat(StatId.H, 90);
        exposedController.State.SetFlag("father_file_found", FlagValue.FromBoolean(true));
        exposedController.State.SetFlag("sonya_exposed", FlagValue.FromBoolean(true));

        Assert.DoesNotContain("D", exposedController.GetAvailableEndingBranches());
    }

    /// <summary>T09/T10 (раздел I Библии): H=69 — ветка C доступна (father_file_found не зависит от H),
    /// ветка D ещё скрыта; H=70 с теми же прочими условиями — обе доступны одновременно.</summary>
    [Fact]
    public void Gate10_BranchC_IndependentOfH_BranchD_HiddenAtH69_VisibleAtH70()
    {
        var below = CreateController();
        below.State.SetStat(StatId.H, 69);
        below.State.SetFlag("father_file_found", FlagValue.FromBoolean(true));
        below.State.SetFlag("sonya_exposed", FlagValue.FromBoolean(false));
        var belowBranches = below.GetAvailableEndingBranches();
        Assert.Contains("C", belowBranches);
        Assert.DoesNotContain("D", belowBranches);

        var at = CreateController();
        at.State.SetStat(StatId.H, 70);
        at.State.SetFlag("father_file_found", FlagValue.FromBoolean(true));
        at.State.SetFlag("sonya_exposed", FlagValue.FromBoolean(false));
        var atBranches = at.GetAvailableEndingBranches();
        Assert.Contains("C", atBranches);
        Assert.Contains("D", atBranches);
    }

    /// <summary>T12 (раздел I Библии): выбор Gate10 фиксируется один раз — повторное применение
    /// того же choiceId (эквивалент перезагрузки сохранения на этой же сцене) не дублирует эффекты.</summary>
    [Fact]
    public void Gate10_BranchSelection_IsIdempotent_OnReappliedChoice()
    {
        var controller = CreateController();
        controller.State.SetStat(StatId.K, 60);
        controller.Start("CH10_SHADOW_COUNCIL");
        AdvanceToChoice(controller);

        controller.SelectChoice("C10_BRANCH_B_REFUSE_THREATEN"); // +20K/-20L -> K80 L30
        Assert.Equal(80, controller.State.GetStat(StatId.K));
        Assert.Equal(30, controller.State.GetStat(StatId.L));

        // Имитация возобновления сохранения ровно на узле Gate10 (тот же GameState, включая
        // appliedChoiceIds) и повторный выбор той же ветки — эффекты не должны начислиться дважды.
        controller.Resume("CH10_SHADOW_COUNCIL", "CH10_SHADOW_COUNCIL_GATE");
        controller.SelectChoice("C10_BRANCH_B_REFUSE_THREATEN");
        Assert.Equal(80, controller.State.GetStat(StatId.K));
        Assert.Equal(30, controller.State.GetStat(StatId.L));
    }
}
