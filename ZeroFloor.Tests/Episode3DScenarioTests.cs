using ZeroFloor.Engine;
using ZeroFloor.Models;
using ZeroFloor.Services;
using ZeroFloor.ViewModels;

namespace ZeroFloor.Tests;

/// <summary>
/// Интерактивные 3D-эпизоды Глав 2А («Ярмарка теней») и 6 («Шахты»): связь со сценарием.
/// Сам 3D-код — Presentation-слой и проверяется автопрогоном отдельно; здесь — то, что обязано
/// оставаться верным при любых изменениях данных: маршруты = реальные выборы узла, исходы
/// эпизода сдвигают шкалы не более чем на ±10 за эффект, провал эпизода не блокирует главу.
/// </summary>
public sealed class Episode3DScenarioTests
{
    private static GameSessionController At(string sceneId, string nodeId, GameState? state = null)
    {
        var c = RealScenarioContentTests.CreateController(state);
        c.Resume(sceneId, nodeId);
        return c;
    }

    [Fact]
    public void Chapter2_DroneNode_CarriesMarketEpisode_WithBothRoutes()
    {
        var c = At("CH02_SC01", "CH02_SC01_D002");
        var cue = c.CurrentNode.MinigameCue;
        Assert.NotNull(cue);
        Assert.Equal(MinigameKind.MarketStealth3D, cue!.Kind);
        Assert.Equal("CH02_SC01_C001_A", cue.Episode3D!.RouteChoiceIds["detour"]);
        Assert.Equal("CH02_SC01_C001_B", cue.Episode3D.RouteChoiceIds["direct"]);
        Assert.False(cue.Episode3D.CommitRouteOnEntry);
    }

    [Fact]
    public void Chapter2_Detected_LeadsToGosha_LowersL_AndKeepsTrustUntouched()
    {
        var c = At("CH02_SC01", "CH02_SC01_D002");
        var cue = c.CurrentNode.MinigameCue!.Episode3D!;
        var before = Enum.GetValues<StatId>().ToDictionary(s => s, s => c.State.GetStat(s));

        c.ApplyMinigameOutcomeEffects(cue.OutcomeEffects["DETECTED"]);
        c.JumpToNode(cue.OutcomeNodeIds["DETECTED"]);
        RealScenarioContentTests.AdvanceToChoice(c);

        Assert.Equal("CH02_SC02", c.State.CurrentSceneId);
        Assert.Equal("DETECTED", c.State.GetFlag("mg_ch2_drone_result")!.Value.StringValue);
        Assert.Null(c.State.GetFlag("gosha_trust"));
        Assert.Equal(before[StatId.L] - 5, c.State.GetStat(StatId.L));
        Assert.Equal(before[StatId.K], c.State.GetStat(StatId.K));
        Assert.Equal(before[StatId.R], c.State.GetStat(StatId.R));
        Assert.Equal(before[StatId.H], c.State.GetStat(StatId.H));
    }

    [Theory]
    [InlineData("ventilation", "C06_ROUTE_VENTILATION", 10, true)]
    [InlineData("residential", "C06_ROUTE_RESIDENTIAL", -10, false)]
    public void Chapter6_PhysicalRoute_IsChoice6_WithOriginalEffects(string route, string choiceId, int deltaH, bool toolLost)
    {
        var c = At("CH06_SHAFTS_CHASE", "CH06_SHAFTS_CHASE_D004");
        var cue = c.CurrentNode.MinigameCue!;
        Assert.Equal(MinigameKind.ShaftsChase3D, cue.Kind);
        Assert.True(cue.Episode3D!.CommitRouteOnEntry);
        Assert.Equal(choiceId, cue.Episode3D.RouteChoiceIds[route]);

        var h = c.State.GetStat(StatId.H);
        c.SelectChoice(choiceId);
        Assert.Equal(h + deltaH, c.State.GetStat(StatId.H));
        Assert.Equal(toolLost, c.State.GetFlag("tool_lost")?.BooleanValue == true);
    }

    [Theory]
    [InlineData("PERFECT", "CH06_SHAFTS_CHASE_02_D004_PERFECT", StatId.K, 3)]
    [InlineData("ROUGH", "CH06_SHAFTS_CHASE_02_D004_ROUGH", StatId.L, -5)]
    [InlineData("NORMAL", null, StatId.H, 0)]
    public void Chapter6_Outcome_ShiftsOneScale_AndAlwaysReachesChapter7(string outcome, string? extraNode, StatId changed, int delta)
    {
        var c = At("CH06_SHAFTS_CHASE", "CH06_SHAFTS_CHASE_D004");
        var layout = c.CurrentNode.MinigameCue!.Episode3D!;
        c.SelectChoice("C06_ROUTE_RESIDENTIAL");
        var stats = Enum.GetValues<StatId>().ToDictionary(s => s, s => c.State.GetStat(s));
        c.ApplyMinigameOutcomeEffects(layout.OutcomeEffects[outcome]);

        var visited = new List<string>();
        for (var i = 0; i < 20 && c.State.CurrentSceneId != "CH07_MEDALLION"; i++)
        {
            var available = c.GetAvailableChoices();
            if (c.CurrentNode.Choices.Count > 0)
            {
                Assert.Single(available); // служебный гейт исхода — всегда ровно один путь
                c.SelectChoice(available[0].ChoiceId);
            }
            else
            {
                c.Advance();
            }
            visited.Add(c.State.CurrentNodeId!);
        }

        Assert.Equal("CH07_MEDALLION", c.State.CurrentSceneId);
        if (extraNode is null)
            Assert.DoesNotContain(visited, v => v.Contains("D004_PERFECT") || v.Contains("D004_ROUGH"));
        else
            Assert.Contains(extraNode, visited);
        foreach (var (stat, value) in stats)
            Assert.Equal(stat == changed ? value + delta : value, c.State.GetStat(stat));
    }

    [Fact]
    public void MinigameOutcome_MayShiftScalesUpTo10_AndSetAnyFlag_ButNotMore()
    {
        var c = At("CH02_SC01", "CH02_SC01_D002");
        var h = c.State.GetStat(StatId.H);
        c.ApplyMinigameOutcomeEffects(new[]
        {
            new Effect { Type = EffectType.AddStat, Key = "H", NumericValue = 10 },
            new Effect { Type = EffectType.SetFlag, Key = "gosha_trust", FlagValue = FlagValue.FromBoolean(true) }
        });
        Assert.Equal(h + 10, c.State.GetStat(StatId.H));
        Assert.True(c.State.GetFlag("gosha_trust")!.Value.BooleanValue);

        Assert.Throws<ScenarioValidationException>(() => c.ApplyMinigameOutcomeEffects(new[]
        {
            new Effect { Type = EffectType.AddStat, Key = "K", NumericValue = 11 }
        }));
        Assert.Throws<ScenarioValidationException>(() => c.ApplyMinigameOutcomeEffects(new[]
        {
            new Effect { Type = EffectType.SetStat, Key = "K", NumericValue = 90 }
        }));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Chapter9_GoshaWrenchLine_ShownOnlyIfWrenchKept(bool toolLost, bool expectLine)
    {
        var state = new GameState();
        if (toolLost) state.SetFlag("tool_lost", FlagValue.FromBoolean(true));
        var c = At("CH09_DEBTOR_GAME", "CH09_DEBTOR_GAME_D002_SKIP", state);
        c.Advance();
        var available = c.GetAvailableChoices();
        Assert.Single(available);
        c.SelectChoice(available[0].ChoiceId);
        Assert.Equal(expectLine, c.State.CurrentNodeId == "CH09_DEBTOR_GAME_D005_TOOL_LINE");
    }

    [Fact]
    public void Validator_RejectsEpisodeOutcomeAboveTenPoints()
    {
        var node = new DialogueNode
        {
            NodeId = "N1",
            Speaker = "narrator",
            Text = "t",
            Choices = new[]
            {
                new ChoiceDefinition { ChoiceId = "A", Text = "a", NextNodeId = "N1" },
                new ChoiceDefinition { ChoiceId = "B", Text = "b", NextNodeId = "N1" }
            },
            MinigameCue = new MinigameCue
            {
                Kind = MinigameKind.MarketStealth3D,
                Episode3D = new Episode3DLayout
                {
                    RouteChoiceIds = new Dictionary<string, string> { ["detour"] = "A", ["direct"] = "B" },
                    OutcomeEffects = new Dictionary<string, IReadOnlyList<Effect>>
                    {
                        ["CLEAN"] = new[] { new Effect { Type = EffectType.AddStat, Key = "K", NumericValue = 15 } },
                        ["DETECTED"] = Array.Empty<Effect>()
                    }
                }
            }
        };
        var scene = new SceneDefinition { SceneId = "S1", Nodes = new[] { node } };
        Assert.Throws<ScenarioValidationException>(() => new ScenarioValidator().Validate(new[] { scene }));
    }

    [Fact]
    public void Chapter8_Archive_IsEpisodeWithoutRoutes_AndContinuesToVictorsLine()
    {
        var c = At("CH08_WATCHERS_ARCHIVE", "CH08_WATCHERS_ARCHIVE_D002");
        var cue = c.CurrentNode.MinigameCue!;
        Assert.Equal(MinigameKind.ArchiveStealth3D, cue.Kind);
        Assert.Empty(cue.Episode3D!.RouteChoiceIds);
        Assert.DoesNotContain("без стелс-механики", c.CurrentNode.Text);

        var l = c.State.GetStat(StatId.L);
        c.ApplyMinigameOutcomeEffects(cue.Episode3D.OutcomeEffects["SPOTTED"]);
        c.Advance();
        Assert.Equal("CH08_WATCHERS_ARCHIVE_D003", c.State.CurrentNodeId);
        Assert.True(c.State.GetFlag("mg_ch8_archive_alert")!.Value.BooleanValue);
        Assert.Equal(l - 5, c.State.GetStat(StatId.L));
    }

    [Fact]
    public void Chapter8_Dossier_MapsToExistingChoices()
    {
        var c = At("CH08_WATCHERS_ARCHIVE_02", "CH08_WATCHERS_ARCHIVE_02_D001");
        var dossier = c.CurrentNode.MinigameCue!.DossierAssembly!;
        Assert.Equal("C08_MATCH_FRAGMENTS", dossier.SuccessChoiceId);
        Assert.Equal("C08_GUESS_RANDOMLY", dossier.FailureChoiceId);
        Assert.Equal(8, dossier.Fragments.Count);

    }

    [Fact]
    public void Chapter8_VerifiedArchiveRecord_UnlocksSuccessorAndBalanceWhenEligible()
    {
        var c = At("CH08_WATCHERS_ARCHIVE_03", "CH08_WATCHERS_ARCHIVE_03_D001B_ASSEMBLE");
        c.State.SetStat(StatId.H, 65);
        Assert.Equal(MinigameKind.ArchiveRecordVerification, c.CurrentNode.MinigameCue!.Kind);
        var game = new ArchiveRecordVerificationViewModel(false, c.Advance, () => Assert.Fail("Unexpected failure"));
        Assert.False(game.ConfirmCommand.CanExecute(null));
        Assert.Equal(3, game.AttemptsRemaining);

        game.SelectedIndex = 0;
        game.ConfirmCommand.Execute(null);
        Assert.Equal(2, game.AttemptsRemaining);
        Assert.Equal("Сверка 1 из 3", game.Stage);
        Assert.NotEmpty(game.Feedback);

        foreach (var answer in new[] { 1, 0, 2 })
        {
            game.SelectedIndex = answer;
            game.ConfirmCommand.Execute(null);
        }

        Assert.True(game.IsVerified);
        Assert.Null(c.State.GetFlag("father_file_found"));
        game.ConfirmCommand.Execute(null);
        game.ConfirmCommand.Execute(null);
        Assert.Equal("CH08_WATCHERS_ARCHIVE_03_D002_OPEN", c.CurrentNode.NodeId);
        Assert.True(c.State.GetFlag("father_file_found")?.BooleanValue == true);
        Assert.Equal(70, c.State.GetStat(StatId.H));
        Assert.Contains("C", c.GetAvailableEndingBranches());
        Assert.Contains("D", c.GetAvailableEndingBranches());
    }

    [Fact]
    public void Chapter8_ArchiveAlert_AllowsTwoMistakesBeforeLosingTheRecord()
    {
        var c = At("CH08_WATCHERS_ARCHIVE_03", "CH08_WATCHERS_ARCHIVE_03_D001B_ASSEMBLE");
        var failure = c.CurrentNode.MinigameCue!.FailureNodeId!;
        var game = new ArchiveRecordVerificationViewModel(true, () => Assert.Fail("Unexpected success"),
            () => c.JumpToNode(failure));
        Assert.Equal(2, game.AttemptsRemaining);

        foreach (var wrongAnswer in new[] { 0, 2 })
        {
            game.SelectedIndex = wrongAnswer;
            game.ConfirmCommand.Execute(null);
        }

        Assert.Equal(failure, c.CurrentNode.NodeId);
        c.Advance();
        Assert.False(c.State.GetFlag("father_file_found")?.BooleanValue);
        Assert.DoesNotContain("C", c.GetAvailableEndingBranches());
        Assert.DoesNotContain("D", c.GetAvailableEndingBranches());
    }
}
