using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZeroFloor.Models;
using ZeroFloor.Services.Presentation;
using ZeroFloor.Services.Presentation.World3D;
using ZeroFloor.ViewModels;

namespace ZeroFloor.Views.Controls;

public partial class IllustratedEpisodeOverlayControl : UserControl
{
    private static readonly Brush AlertBrush = new SolidColorBrush(Color.FromRgb(255, 148, 104));
    private readonly Stopwatch _clock = new();
    private NovelViewModel? _novel;
    private Episode3DViewModel? _episode;
    private IllustratedEpisode? _scene;
    private EpisodeAudio? _audio;
    private Window? _window;
    private Point? _destination;
    private bool _paused;
    private bool _finishing;
    private double _lastStep;

    public IllustratedEpisodeOverlayControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
                _window.Deactivated += OnWindowDeactivated;
            Subscribe(DataContext as NovelViewModel);
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
                _window.Deactivated -= OnWindowDeactivated;
            _window = null;
            Subscribe(null);
        };
        DataContextChanged += (_, args) =>
        {
            if (IsLoaded)
                Subscribe(args.NewValue as NovelViewModel);
        };
    }

    private void Subscribe(NovelViewModel? novel)
    {
        if (_novel is not null)
            _novel.PropertyChanged -= OnNovelChanged;
        _novel = novel;
        if (_novel is not null)
            _novel.PropertyChanged += OnNovelChanged;
        SyncEpisode();
    }

    private void OnNovelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NovelViewModel.ActiveEpisode))
            SyncEpisode();
    }

    private void SyncEpisode()
    {
        var next = _novel?.ActiveEpisode;
        if (ReferenceEquals(_episode, next))
            return;
        if (_episode is not null)
            _episode.PropertyChanged -= OnEpisodeChanged;
        CompositionTarget.Rendering -= OnFrame;
        _audio?.Dispose();
        _audio = null;
        _episode = next;
        _scene = next is null ? null : new IllustratedEpisode(next.Kind);
        Stage.SetScene(_scene);

        Fade.BeginAnimation(OpacityProperty, null);
        Fade.Opacity = 0;
        PausePanel.Visibility = Visibility.Collapsed;
        _paused = false;
        _finishing = false;
        _destination = null;
        _lastStep = 0;

        if (_scene is null || next is null)
            return;
        next.PropertyChanged += OnEpisodeChanged;
        _audio = new EpisodeAudio(App.Current.Settings);
        LocationText.Text = _scene.Location;
        InstructionText.Text = _scene.Instruction;
        HintText.Text = _scene.IsChase
            ? "WASD / стрелки — идти     Shift — рывок     E — выход     Щелчок — идти к точке"
            : "WASD / стрелки — идти     E — взаимодействие     Щелчок — идти к точке";
        RefreshHints();
        UpdateHud(_scene);
        _clock.Restart();
        Fade.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450)));
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnEpisodeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Episode3DViewModel.ShowControlHints))
            RefreshHints();
    }

    private void RefreshHints() =>
        Hints.Visibility = _episode?.ShowControlHints == false ? Visibility.Collapsed : Visibility.Visible;

    private void OnWindowDeactivated(object? sender, EventArgs args) => SetPaused(true);

    private void OnPauseClick(object sender, RoutedEventArgs args) => SetPaused(!_paused);

    private void SetPaused(bool paused)
    {
        if (_scene is null || _finishing || _paused == paused)
            return;
        _paused = paused;
        _destination = null;
        _audio?.SetPaused(paused);
        PausePanel.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        _clock.Restart();
        if (!paused)
            Focus();
    }

    internal bool HandleKey(KeyEventArgs args)
    {
        if (_scene is null)
            return false;
        if (!args.IsRepeat && args.Key == Key.Escape)
        {
            SetPaused(!_paused);
            return true;
        }
        if (_paused)
            return false;
        if (!args.IsRepeat && args.Key == Key.E)
            FinishIfNear();
        return true;
    }

    private void OnStageClick(object sender, MouseButtonEventArgs args)
    {
        if (_paused || _finishing || _scene is not { } scene)
            return;
        var point = args.GetPosition(Stage);
        var marker = scene.Exits.FirstOrDefault(exit => (point - exit.Position).Length < 56);
        if (marker is not null && IllustratedEpisode.InReach(marker, scene.Position))
            FinishIfNear();
        else if (marker is not null || scene.Floor.Contains(point))
            _destination = marker?.Position ?? point;
    }

    private static bool IsHeld(Key first, Key second) => Keyboard.IsKeyDown(first) || Keyboard.IsKeyDown(second);

    private void OnFrame(object? sender, EventArgs args)
    {
        var dt = Math.Min(_clock.Elapsed.TotalSeconds, 0.05);
        _clock.Restart();
        if (_paused || _finishing || _scene is not { } scene)
            return;

        var direction = new Vector(
            Convert.ToInt32(IsHeld(Key.D, Key.Right)) - Convert.ToInt32(IsHeld(Key.A, Key.Left)),
            Convert.ToInt32(IsHeld(Key.S, Key.Down)) - Convert.ToInt32(IsHeld(Key.W, Key.Up)));
        if (direction.LengthSquared > 0)
            _destination = null;
        else if (_destination is { } target)
        {
            var remaining = target - scene.Position;
            if (remaining.Length < 7)
                _destination = null;
            else
                direction = new Vector(remaining.X, remaining.Y / 0.72);
        }

        var wasDetected = scene.Detected;
        var sprint = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        scene.Step(dt, direction, sprint);
        if (scene.Moving && scene.Time - _lastStep > (scene.IsChase && sprint ? 0.22 : 0.34))
        {
            _audio?.Play(scene.Kind == MinigameKind.MarketStealth3D ? "step_concrete" : "step_metal", 0.27);
            _lastStep = scene.Time;
        }
        if (!wasDetected && scene.Detected)
            _audio?.Play("alarm", 0.35);

        UpdateHud(scene);
        Stage.InvalidateVisual();
    }

    private void UpdateHud(IllustratedEpisode scene)
    {
        RiskBar.Value = scene.IsChase ? scene.Pursuit : scene.Exposure;
        RiskBar.Foreground = RiskBar.Value >= 0.55 ? AlertBrush : (Brush)FindResource("Mint");
        StatusText.Text = scene.IsChase ? "ПАТРУЛЬ ПРИБЛИЖАЕТСЯ" : "ЗАМЕТНОСТЬ";
        ScanWarning.Visibility = scene.Detected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FinishIfNear()
    {
        if (_paused || _finishing || _episode is not { } episode ||
            _scene is not { NearbyExit: { } exit } scene)
            return;

        _finishing = true;
        var route = scene.Kind == MinigameKind.MarketStealth3D ? scene.MarketRoute : exit.Route;
        if (scene.IsChase)
            episode.CommitRoute(route);
        _audio?.Play("door", 0.55);
        var fade = new DoubleAnimation(Fade.Opacity, 1, TimeSpan.FromMilliseconds(280));
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(episode, _episode))
                episode.Complete(scene.Outcome, route);
        };
        Fade.BeginAnimation(OpacityProperty, fade);
    }
}
