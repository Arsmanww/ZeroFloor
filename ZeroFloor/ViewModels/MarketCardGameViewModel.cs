using System.Collections.ObjectModel;
using System.Windows.Input;
using ZeroFloor.Common;

namespace ZeroFloor.ViewModels;

public sealed record MarketPlayingCard(string Rank, string Suit, int Value)
{
    public bool IsRed => Suit is "♥" or "♦";
    public bool IsFaceDown => Value == 0;
}

public sealed class MarketCardGameViewModel : ViewModelBase
{
    private readonly Action _onWin;
    private readonly Action _onLoss;
    private readonly MarketPlayingCard _hiddenCard;
    private readonly MarketPlayingCard _drawCard;
    private readonly ObservableCollection<MarketPlayingCard> _maxHand;
    private readonly ObservableCollection<MarketPlayingCard> _goshaHand;
    private bool _resolved;
    private bool _completed;
    private bool _won;
    private string _result = string.Empty;

    public MarketCardGameViewModel(Action onWin, Action onLoss, Random? random = null)
    {
        _onWin = onWin;
        _onLoss = onLoss;
        var deck = (from suit in new[] { "♣", "♠", "♥", "♦" }
                    from rank in new[] { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" }
                    select new MarketPlayingCard(rank, suit, rank switch
                    {
                        "A" => 1,
                        "J" or "Q" or "K" => 10,
                        _ => int.Parse(rank)
                    })).ToArray();
        (random ?? Random.Shared).Shuffle(deck.AsSpan());
        _maxHand = new ObservableCollection<MarketPlayingCard>(deck[..2]);
        _goshaHand = [deck[2], new MarketPlayingCard("", "", 0)];
        MaxHand = new ReadOnlyObservableCollection<MarketPlayingCard>(_maxHand);
        GoshaHand = new ReadOnlyObservableCollection<MarketPlayingCard>(_goshaHand);
        _hiddenCard = deck[3];
        _drawCard = deck[4];

        StandCommand = new RelayCommand(() => Resolve(draw: false), () => CanPlay);
        DrawCommand = new RelayCommand(() => Resolve(draw: true), () => CanPlay);
        ContinueCommand = new RelayCommand(Complete, () => IsResolved && !_completed);
    }

    public ReadOnlyObservableCollection<MarketPlayingCard> MaxHand { get; }
    public ReadOnlyObservableCollection<MarketPlayingCard> GoshaHand { get; }
    public bool IsResolved => _resolved;
    public bool CanPlay => !IsResolved;
    public string MaxLabel => $"Макс · {Score(_maxHand)}";
    public string GoshaLabel => IsResolved ? $"Гоша · {Score(_goshaHand)}" : "Гоша";
    public string Result => _result;
    public ICommand StandCommand { get; }
    public ICommand DrawCommand { get; }
    public ICommand ContinueCommand { get; }

    private void Resolve(bool draw)
    {
        if (IsResolved)
            return;

        if (draw)
            _maxHand.Add(_drawCard);
        _goshaHand[1] = _hiddenCard;

        var max = Score(_maxHand);
        var gosha = Score(_goshaHand);
        _won = max <= 21 && (gosha > 21 || max > gosha);
        _result = _won ? "Макс выиграл." : max > 21
            ? "Перебор. Один паёк в долг Гоше."
            : max == gosha ? "Ничья в пользу Гоши. Один паёк в долг."
            : "Гоша выиграл. Один паёк в долг.";

        SetProperty(ref _resolved, true, nameof(IsResolved));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(MaxLabel));
        OnPropertyChanged(nameof(GoshaLabel));
        OnPropertyChanged(nameof(Result));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Complete()
    {
        if (!IsResolved || _completed)
            return;

        _completed = true;
        if (_won)
            _onWin();
        else
            _onLoss();
    }

    private static int Score(IEnumerable<MarketPlayingCard> hand)
    {
        var sum = hand.Sum(card => card.Value);
        return sum <= 11 && hand.Any(card => card.Rank == "A") ? sum + 10 : sum;
    }
}
