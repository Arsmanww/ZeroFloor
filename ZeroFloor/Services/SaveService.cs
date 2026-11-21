using System.IO;
using System.Text.Json;
using ZeroFloor.Models;
using ZeroFloor.Services.Json;

namespace ZeroFloor.Services;

/// <summary>
/// Файловая реализация <see cref="ISaveService"/> (TECHNICAL_ARCHITECTURE.md, раздел 9):
/// ≥10 ручных слотов (<see cref="ManualSlotCount"/>) + одно отдельное автосохранение в своём
/// файле — поэтому автосохранение физически не может перезаписать ручной слот. Запись —
/// во временный файл рядом со слотом, затем атомарная замена (<see cref="File.Move(string,string,bool)"/>
/// с overwrite) — повреждение процесса посреди записи не портит уже существующий слот.
/// Повреждённый/нечитаемый файл слота не ломает получение списка остальных слотов —
/// см. <see cref="ReadSummary"/>.
/// </summary>
public sealed class SaveService : ISaveService
{
    public const int SaveVersion = 1;

    private static readonly JsonSerializerOptions Options = SaveJsonOptions.Create();

    private readonly string _savesDirectory;

    public SaveService(string savesDirectory)
    {
        _savesDirectory = savesDirectory;
        Directory.CreateDirectory(_savesDirectory);
    }

    public int ManualSlotCount { get; } = 10;

    public int CurrentSaveVersion => SaveVersion;

    public bool HasAnySave =>
        GetManualSlotSummaries().Any(summary => summary.IsOccupied) || GetAutoSaveSummary().IsOccupied;

    public IReadOnlyList<SaveSlotSummary> GetManualSlotSummaries()
    {
        var result = new List<SaveSlotSummary>(ManualSlotCount);

        for (var index = 1; index <= ManualSlotCount; index++)
            result.Add(ReadSummary(SaveSlotKind.Manual, index, ManualSlotPath(index)));

        return result;
    }

    public SaveSlotSummary GetAutoSaveSummary() => ReadSummary(SaveSlotKind.Auto, 0, AutoSavePath);

    public SaveSlotData LoadManualSlot(int slotIndex)
    {
        EnsureValidSlotIndex(slotIndex);
        var path = ManualSlotPath(slotIndex);

        if (!File.Exists(path))
            throw new SaveLoadException($"Слот сохранения {slotIndex} пуст.");

        return ReadFile(path);
    }

    public SaveSlotData LoadAutoSave()
    {
        if (!File.Exists(AutoSavePath))
            throw new SaveLoadException("Автосохранение отсутствует.");

        return ReadFile(AutoSavePath);
    }

    public void SaveManualSlot(int slotIndex, SaveSlotData data)
    {
        EnsureValidSlotIndex(slotIndex);
        WriteFileAtomic(ManualSlotPath(slotIndex), data);
    }

    public void SaveAutoSave(SaveSlotData data) => WriteFileAtomic(AutoSavePath, data);

    public void DeleteManualSlot(int slotIndex)
    {
        EnsureValidSlotIndex(slotIndex);
        var path = ManualSlotPath(slotIndex);

        if (File.Exists(path))
            File.Delete(path);
    }

    public (SaveSlotKind Kind, int SlotIndex)? FindMostRecentSave()
    {
        (SaveSlotKind Kind, int SlotIndex)? bestRef = null;
        DateTime? bestTimestamp = null;

        foreach (var summary in GetManualSlotSummaries())
        {
            if (!summary.CanLoad)
                continue;

            if (bestTimestamp is null || summary.Timestamp > bestTimestamp)
            {
                bestTimestamp = summary.Timestamp;
                bestRef = (SaveSlotKind.Manual, summary.SlotIndex);
            }
        }

        var autoSummary = GetAutoSaveSummary();
        if (autoSummary.CanLoad && (bestTimestamp is null || autoSummary.Timestamp > bestTimestamp))
            bestRef = (SaveSlotKind.Auto, 0);

        return bestRef;
    }

    private SaveSlotSummary ReadSummary(SaveSlotKind kind, int index, string path)
    {
        if (!File.Exists(path))
            return new SaveSlotSummary(kind, index, false, false, null, 0, null, null, null, null, null);

        try
        {
            var data = ReadFile(path);
            return new SaveSlotSummary(
                kind, index, IsOccupied: true, IsCorrupted: false,
                data.Timestamp, data.PlayTimeSeconds, data.ChapterId, data.SceneId,
                data.LocationSummary, data.CurrentEndingId, CorruptionMessage: null);
        }
        catch (SaveLoadException ex)
        {
            // Повреждённый слот изолируется — остальные слоты остаются читаемыми
            // (CLAUDE.md, правило 8: "защита от повреждённых файлов сохранения").
            return new SaveSlotSummary(kind, index, IsOccupied: true, IsCorrupted: true,
                null, 0, null, null, null, null, ex.Message);
        }
    }

    private static SaveSlotData ReadFile(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new SaveLoadException($"Не удалось прочитать файл сохранения '{path}'.", ex);
        }

        SaveSlotData? data;
        try
        {
            data = JsonSerializer.Deserialize<SaveSlotData>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new SaveLoadException($"Файл сохранения повреждён: '{path}'.", ex);
        }

        if (data is null)
            throw new SaveLoadException($"Файл сохранения пуст или повреждён: '{path}'.");

        if (data.SaveVersion != SaveVersion)
            throw new SaveVersionMismatchException(data.SaveVersion, SaveVersion);

        // Старые слоты могли остаться внутри удалённых сцен или на переходах к ним.
        var (sceneId, nodeId) = (data.SceneId, data.NodeId) switch
        {
            ("CH01_SC01", { } node) when node == "CH01_SC01_D002"
                || node.StartsWith("CH01_SC01_D003B_", StringComparison.Ordinal)
                => ("CH01_SC01", "CH01_SC01_D003"),
            ("CH01_SC01", { } node) when node is "CH01_SC01_D005" or "CH01_SC01_D006" or "CH01_SC01_D009"
                || node.StartsWith("CH01_SC01_D007_", StringComparison.Ordinal)
                || node.StartsWith("CH01_SC01_D008_", StringComparison.Ordinal)
                => ("CH01_SC01", "CH01_SC01_D010"),
            ("CH01_SC02", "CH01_SC02_D002" or "CH01_SC02_D003") => ("CH01_SC02", "CH01_SC02_D001"),
            ("CH01_SC02", "CH01_SC02_D005") => ("CH01_SC02", "CH01_SC02_D006"),
            ("CH02_SC_SIDE01", _) => ("CH03_SC01", "CH03_SC01_D001"),
            ("CH03_BREACH_JOURNALIST", _) or ("CH03_SC04", "CH03_SC04_D004_JOURNALIST_GATE")
                => ("CH04_SC01", "CH04_SC01_D001"),
            ({ } scene, { } node) when (scene is "CH12_FINALE_A_EPILOGUE" or "CH12_FINALE_B_EPILOGUE"
                or "CH12_FINALE_C_EPILOGUE" or "CH12_FINALE_D_EPILOGUE")
                && (node == scene + "_PRESS" || node == scene + "_PRESS_GATE")
                => (scene, scene[..^"_EPILOGUE".Length] + "_TONE"),
            _ => (null, null)
        };

        if (sceneId is null)
            return data;

        data.GameState.CurrentChapterId = sceneId;
        data.GameState.CurrentSceneId = sceneId;
        data.GameState.CurrentNodeId = nodeId;
        return data with { ChapterId = sceneId, SceneId = sceneId, NodeId = nodeId, LocationSummary = null };
    }

    private void WriteFileAtomic(string path, SaveSlotData data)
    {
        var json = JsonSerializer.Serialize(data, Options);
        var tempPath = path + ".tmp";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private string ManualSlotPath(int slotIndex) => Path.Combine(_savesDirectory, $"slot_{slotIndex:00}.json");

    private string AutoSavePath => Path.Combine(_savesDirectory, "autosave.json");

    private void EnsureValidSlotIndex(int slotIndex)
    {
        if (slotIndex < 1 || slotIndex > ManualSlotCount)
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex), slotIndex, $"Индекс слота должен быть от 1 до {ManualSlotCount}.");
    }
}
