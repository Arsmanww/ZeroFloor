using System.IO;
using System.Linq;
using ZeroFloor.Engine;
using ZeroFloor.Models;
using ZeroFloor.Services;

namespace ZeroFloor.Tests;

/// <summary>
/// Проверяет файловые сохранения (SaveService) на разных этапах прохождения — требование
/// "проверь сохранение и загрузку на разных этапах прохождения": сохранение сразу после
/// старта, посреди сцены (после выбора с эффектами), после перехода в следующую сцену —
/// и что повторная загрузка не дублирует эффекты уже применённых выборов (идемпотентность,
/// PROJECT_ANALYSIS T12). Каждый тест работает в своём временном каталоге — тесты не мешают
/// друг другу и не оставляют файлов в репозитории.
/// </summary>
public sealed class SaveServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SaveService _saveService;

    public SaveServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ZeroFloorTests_" + Guid.NewGuid());
        _saveService = new SaveService(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static GameSessionController CreateController(GameState? state = null)
    {
        var repository = new ScenarioRepository(new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample_scenario.json")
        });

        var sceneManager = new SceneManager(repository);
        var effectApplier = new EffectApplier();
        var dialogueManager = new DialogueManager(sceneManager, effectApplier);
        var conditionEvaluator = new ConditionEvaluator();
        var choiceProcessor = new ChoiceProcessor(conditionEvaluator, effectApplier);
        var endingManager = new EndingManager(conditionEvaluator);

        return new GameSessionController(dialogueManager, choiceProcessor, endingManager, state ?? new GameState());
    }

    private static SaveSlotData Snapshot(GameSessionController controller, int playTimeSeconds = 120) => new()
    {
        SaveVersion = SaveService.SaveVersion,
        Timestamp = DateTime.Now,
        PlayTimeSeconds = playTimeSeconds,
        ChapterId = controller.State.CurrentChapterId,
        SceneId = controller.State.CurrentSceneId,
        NodeId = controller.State.CurrentNodeId,
        CurrentEndingId = controller.State.CurrentEndingId,
        LocationSummary = $"{controller.CurrentNode.Speaker}: {controller.CurrentNode.Text}",
        GameState = controller.State
    };

    [Fact]
    public void GetManualSlotSummaries_InitiallyAllEmpty()
    {
        var summaries = _saveService.GetManualSlotSummaries();

        Assert.Equal(10, summaries.Count);
        Assert.All(summaries, s => Assert.False(s.IsOccupied));
        Assert.False(_saveService.HasAnySave);
    }

    [Theory]
    [InlineData("CH01_SC01", "CH01_SC01_D003B_CABLE_CHECK", "CH01_SC01", "CH01_SC01_D003")]
    [InlineData("CH01_SC01", "CH01_SC01_D008_JOKE", "CH01_SC01", "CH01_SC01_D010")]
    [InlineData("CH01_SC02", "CH01_SC02_D005", "CH01_SC02", "CH01_SC02_D006")]
    [InlineData("CH02_SC_SIDE01", "CH02_SC_SIDE01_D002_HELP", "CH03_SC01", "CH03_SC01_D001")]
    [InlineData("CH03_BREACH_JOURNALIST", "CH03_BREACH_JOURNALIST_D002", "CH04_SC01", "CH04_SC01_D001")]
    [InlineData("CH03_SC04", "CH03_SC04_D004_JOURNALIST_GATE", "CH04_SC01", "CH04_SC01_D001")]
    [InlineData("CH12_FINALE_A_EPILOGUE", "CH12_FINALE_A_EPILOGUE_PRESS_GATE", "CH12_FINALE_A_EPILOGUE", "CH12_FINALE_A_TONE")]
    [InlineData("CH12_FINALE_D_EPILOGUE", "CH12_FINALE_D_EPILOGUE_PRESS", "CH12_FINALE_D_EPILOGUE", "CH12_FINALE_D_TONE")]
    public void LoadManualSlot_AtRemovedStoryNode_ResumesWithExistingProgress(
        string oldScene, string oldNode, string nextScene, string nextNode)
    {
        var state = new GameState { CurrentChapterId = oldScene, CurrentSceneId = oldScene, CurrentNodeId = oldNode };
        state.SetStat(StatId.H, 65);
        _saveService.SaveManualSlot(1, new SaveSlotData
        {
            SaveVersion = SaveService.SaveVersion, Timestamp = DateTime.Now, PlayTimeSeconds = 12,
            ChapterId = oldScene, SceneId = oldScene, NodeId = oldNode, GameState = state
        });

        var loaded = _saveService.LoadManualSlot(1);
        Assert.Equal(nextScene, loaded.SceneId);
        Assert.Equal(nextNode, loaded.NodeId);
        Assert.Equal(65, loaded.GameState.GetStat(StatId.H));
        var controller = RealScenarioContentTests.CreateController(loaded.GameState);
        controller.Resume(loaded.SceneId!, loaded.NodeId!);
        Assert.Equal(nextNode, controller.CurrentNode.NodeId);
    }

    [Fact]
    public void SaveAndLoad_AtVeryStart_RoundTripsPositionAndDefaultStats()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");

        _saveService.SaveManualSlot(1, Snapshot(controller));
        var loaded = _saveService.LoadManualSlot(1);

        Assert.Equal("TEST_SCENE_1", loaded.SceneId);
        Assert.Equal("N001", loaded.NodeId);
        Assert.Equal(60, loaded.GameState.GetStat(StatId.H));
        Assert.Empty(loaded.GameState.AppliedChoiceIds);
    }

    [Fact]
    public void SaveAndLoad_MidSceneAfterChoiceWithEffects_RoundTripsStatsFlagsAndHistory()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        controller.Advance(); // N001 -> N002 (узел с выбором)
        controller.SelectChoice("C001_BRAVE"); // +5 H, flag test_brave_chosen=true, -> N003

        _saveService.SaveManualSlot(2, Snapshot(controller));
        var loaded = _saveService.LoadManualSlot(2);

        Assert.Equal(65, loaded.GameState.GetStat(StatId.H));
        Assert.Equal(FlagValue.FromBoolean(true), loaded.GameState.GetFlag("test_brave_chosen"));
        Assert.Contains("C001_BRAVE", loaded.GameState.AppliedChoiceIds);
        Assert.Contains("N001", loaded.GameState.ViewedNodeIds);
        Assert.Contains("N002", loaded.GameState.ViewedNodeIds);

        var historyEntry = Assert.Single(loaded.GameState.ChoiceHistory);
        Assert.Equal("C001_BRAVE", historyEntry.ChoiceId);
        Assert.Equal("TEST_SCENE_1", historyEntry.SceneId);
    }

    [Fact]
    public void ResumeFromLoadedSave_ReselectingSameChoice_DoesNotDoubleApplyEffects()
    {
        // Этап 1: пройти до выбора, применить его и сохранить.
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        controller.Advance();
        controller.SelectChoice("C001_BRAVE");
        _saveService.SaveManualSlot(3, Snapshot(controller));

        // Этап 2: "перезапуск игры" — новый контроллер с состоянием из сейва, возобновлённый
        // ровно на том узле, где игрок сохранился (как при настоящей загрузке).
        var loaded = _saveService.LoadManualSlot(3);
        var resumedController = CreateController(loaded.GameState);
        resumedController.Resume(loaded.SceneId!, loaded.NodeId!);

        Assert.Equal(65, resumedController.State.GetStat(StatId.H));
        Assert.Equal("N003", resumedController.CurrentNode.NodeId);
        Assert.Single(resumedController.State.ChoiceHistory);
    }

    [Fact]
    public void SaveAndLoad_AfterSceneTransition_RoundTripsFlagsFromSecondScene()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        controller.Advance();
        controller.SelectChoice("C001_BRAVE");
        controller.Advance(); // -> TEST_SCENE_2
        controller.SelectChoice("C002_ONLY_IF_BRAVE");

        _saveService.SaveManualSlot(4, Snapshot(controller));
        var loaded = _saveService.LoadManualSlot(4);

        Assert.Equal("TEST_SCENE_2", loaded.SceneId);
        Assert.Equal("N002", loaded.NodeId);
        Assert.Equal(FlagValue.FromBoolean(true), loaded.GameState.GetFlag("test_scene2_done"));
        Assert.Equal(2, loaded.GameState.ChoiceHistory.Count);
    }

    [Fact]
    public void SaveAndLoad_PreservesDialogueLogAcrossReload()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        controller.State.AppendToDialogueLog(new DialogueLogRecord(
            DialogueLogEntryKind.Line, "TEST_SCENE_1", "N001", "narrator", "Вступительная реплика.", DateTime.Now));

        _saveService.SaveManualSlot(5, Snapshot(controller));
        var loaded = _saveService.LoadManualSlot(5);

        var logEntry = Assert.Single(loaded.GameState.DialogueLog);
        Assert.Equal("narrator", logEntry.Speaker);
        Assert.Equal(DialogueLogEntryKind.Line, logEntry.Kind);
    }

    [Fact]
    public void ManualSaveAndAutoSave_AreIndependentSlots()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");

        _saveService.SaveManualSlot(1, Snapshot(controller, playTimeSeconds: 10));
        controller.Advance();
        _saveService.SaveAutoSave(Snapshot(controller, playTimeSeconds: 20));

        var manual = _saveService.LoadManualSlot(1);
        var auto = _saveService.LoadAutoSave();

        Assert.Equal("N001", manual.NodeId);
        Assert.Equal("N002", auto.NodeId);
        Assert.Equal(10, manual.PlayTimeSeconds);
        Assert.Equal(20, auto.PlayTimeSeconds);
    }

    [Fact]
    public void Overwrite_ReplacesSlotContentCompletely()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        _saveService.SaveManualSlot(1, Snapshot(controller));

        controller.Advance();
        controller.SelectChoice("C001_CAUTIOUS");
        _saveService.SaveManualSlot(1, Snapshot(controller));

        var loaded = _saveService.LoadManualSlot(1);
        Assert.Equal("N003", loaded.NodeId);
        Assert.Equal(55, loaded.GameState.GetStat(StatId.H));
    }

    [Fact]
    public void DeleteManualSlot_ClearsSlot()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        _saveService.SaveManualSlot(1, Snapshot(controller));

        _saveService.DeleteManualSlot(1);

        Assert.Throws<SaveLoadException>(() => _saveService.LoadManualSlot(1));
        Assert.False(_saveService.GetManualSlotSummaries()[0].IsOccupied);
    }

    [Fact]
    public void LoadManualSlot_EmptySlot_ThrowsSaveLoadException()
    {
        Assert.Throws<SaveLoadException>(() => _saveService.LoadManualSlot(1));
    }

    [Fact]
    public void CorruptedSlotFile_IsIsolated_DoesNotBreakOtherSlots()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        _saveService.SaveManualSlot(1, Snapshot(controller));
        _saveService.SaveManualSlot(2, Snapshot(controller));

        // Повреждаем файл слота 2 напрямую — имитация сбоя диска/ручного редактирования.
        var corruptedPath = Path.Combine(_tempDirectory, "slot_02.json");
        File.WriteAllText(corruptedPath, "{ this is not valid json");

        var summaries = _saveService.GetManualSlotSummaries();

        var slot1 = summaries.Single(s => s.SlotIndex == 1);
        var slot2 = summaries.Single(s => s.SlotIndex == 2);

        Assert.True(slot1.IsOccupied);
        Assert.False(slot1.IsCorrupted);

        Assert.True(slot2.IsOccupied);
        Assert.True(slot2.IsCorrupted);
        Assert.NotNull(slot2.CorruptionMessage);

        // Слот 1 всё ещё нормально загружается — повреждение слота 2 его не затронуло.
        var loadedSlot1 = _saveService.LoadManualSlot(1);
        Assert.Equal("TEST_SCENE_1", loadedSlot1.SceneId);

        Assert.Throws<SaveLoadException>(() => _saveService.LoadManualSlot(2));
    }

    [Fact]
    public void VersionMismatch_ThrowsSpecificException()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");
        _saveService.SaveManualSlot(1, Snapshot(controller));

        var path = Path.Combine(_tempDirectory, "slot_01.json");
        var json = File.ReadAllText(path).Replace("\"saveVersion\": 1", "\"saveVersion\": 999");
        File.WriteAllText(path, json);

        var ex = Assert.Throws<SaveVersionMismatchException>(() => _saveService.LoadManualSlot(1));
        Assert.Equal(999, ex.FoundVersion);
        Assert.Equal(1, ex.ExpectedVersion);
    }

    [Fact]
    public void FindMostRecentSave_PicksLatestAmongManualAndAuto_IgnoringCorrupted()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");

        _saveService.SaveManualSlot(1, Snapshot(controller) with { Timestamp = DateTime.Now.AddMinutes(-10) });
        _saveService.SaveManualSlot(2, Snapshot(controller) with { Timestamp = DateTime.Now.AddMinutes(-1) });
        _saveService.SaveAutoSave(Snapshot(controller) with { Timestamp = DateTime.Now.AddMinutes(-5) });

        var mostRecent = _saveService.FindMostRecentSave();

        Assert.NotNull(mostRecent);
        Assert.Equal(SaveSlotKind.Manual, mostRecent!.Value.Kind);
        Assert.Equal(2, mostRecent.Value.SlotIndex);
    }

    [Fact]
    public void EnsureValidSlotIndex_RejectsOutOfRange()
    {
        var controller = CreateController();
        controller.Start("TEST_SCENE_1");

        Assert.Throws<ArgumentOutOfRangeException>(() => _saveService.SaveManualSlot(0, Snapshot(controller)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _saveService.SaveManualSlot(11, Snapshot(controller)));
    }
}
