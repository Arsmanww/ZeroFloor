using ZeroFloor.Models;
using ZeroFloor.ViewModels;

namespace ZeroFloor.Tests;

public sealed class DebtorMinigameTests
{
    [Fact]
    public void BothWitnessesAndTheirCommonPassageLeadToTheExistingRescueChoice()
    {
        var controller = RealScenarioContentTests.CreateController();
        controller.Resume("CH09_DEBTOR", "CH09_DEBTOR_D006_WITNESS");
        Assert.Equal(MinigameKind.MarketWitnessRoute, controller.CurrentNode.MinigameCue!.Kind);
        var game = new MarketWitnessRouteViewModel(controller.Advance);

        game.MarkLoaderCommand.Execute(null);
        Assert.False(game.SelectPointCommand.CanExecute("passage"));
        game.MarkTraderCommand.Execute(null);
        game.SelectPointCommand.Execute("gate");
        game.ConfirmCommand.Execute(null);
        Assert.False(game.IsSolved);

        game.SelectPointCommand.Execute("passage");
        game.ConfirmCommand.Execute(null);
        Assert.True(game.IsSolved);
        game.ConfirmCommand.Execute(null);
        RealScenarioContentTests.AdvanceToChoice(controller);
        Assert.Equal("CH09_DEBTOR_D007_PATHS", controller.CurrentNode.NodeId);
        Assert.Contains(controller.GetAvailableChoices(), choice => choice.ChoiceId == "C09_PATH_FORCE");
        Assert.Null(controller.State.GetFlag("dan_status"));
    }

    [Theory]
    [InlineData(false, "win", 0, 10)]
    [InlineData(true, "loss_debt", 1, 5)]
    public void WithTheSameDealDrawingCanTurnAWinningHandIntoDebt(
        bool draw, string outcome, int debt, int reputationGain)
    {
        var controller = RealScenarioContentTests.CreateController();
        controller.Resume("CH09_DEBTOR_GAME", "CH09_DEBTOR_GAME_D002_PLAY");
        var cue = controller.CurrentNode.MinigameCue!;
        Assert.Equal(MinigameKind.MarketCardGame, cue.Kind);
        var game = new MarketCardGameViewModel(controller.Advance,
            () => controller.JumpToNode(cue.FailureNodeId!), new Random(1));

        Assert.True(game.GoshaHand[1].IsFaceDown);
        (draw ? game.DrawCommand : game.StandCommand).Execute(null);
        Assert.False(game.GoshaHand[1].IsFaceDown);
        Assert.Equal(draw ? 3 : 2, game.MaxHand.Count);
        Assert.Null(controller.State.GetFlag("market_debt"));

        game.ContinueCommand.Execute(null);
        game.ContinueCommand.Execute(null);
        Assert.Equal(outcome, controller.State.GetFlag("underground_game_outcome")?.StringValue);
        Assert.Equal(debt, controller.State.GetFlag("market_debt")?.IntegerValue);
        Assert.Equal(50 + reputationGain, controller.State.GetStat(StatId.R));
    }
}
