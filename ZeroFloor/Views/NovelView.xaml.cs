using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ZeroFloor.Models;
using ZeroFloor.Services;
using ZeroFloor.Services.Presentation;
using ZeroFloor.ViewModels;

namespace ZeroFloor.Views;

public partial class NovelView : UserControl
{
    private static readonly Random Rng = new();

    // Условные "фоны" демонстрации — реальных изображений ещё нет (см. CLAUDE.md), поэтому
    // вместо id картинки подставляется один из нескольких узнаваемо разных градиентов, чтобы
    // кроссфейд между сценами было видно даже без растровых ассетов.
    private static readonly Dictionary<string, Brush> DemoBackgrounds = new()
    {
        ["demo_bg_dusk"] = CreateGradient("#171C28", "#0A0C11"),
        ["demo_bg_industrial"] = CreateGradient("#2B2318", "#120E08"),
        ["demo_bg_night"] = CreateGradient("#1A1430", "#08060F"),
        ["demo_bg_dawn"] = CreateGradient("#3A2B3E", "#15101F")
    };

    private bool _isLayerAActive = true;
    private bool _hasPlayedOpeningReveal;

    public NovelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is NovelViewModel oldViewModel)
            oldViewModel.AnimationCuesReady -= OnAnimationCuesReady;
        if (e.OldValue is NovelViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is NovelViewModel newViewModel)
        {
            newViewModel.AnimationCuesReady += OnAnimationCuesReady;
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Конструктор ViewModel уже мог отправить первые реплики-ремарки до того, как
            // View успел подписаться, — отыгрываем последнее известное состояние явно.
            if (newViewModel.LastAnimationCues is { } cues)
                _ = HandleCuesAsync(cues);
        }
    }

    private void OnAnimationCuesReady(object? sender, NodeAnimationCues cues) => _ = HandleCuesAsync(cues);

    private bool _episodeWasActive;

    /// <summary>Возврат из интерактивного эпизода к сюжетной сцене: эпизод уходит в чёрное сам, а новелла
    /// плавно проявляется из чёрного — переход без резкой смены кадра.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NovelViewModel.ActiveEpisode) || sender is not NovelViewModel vm)
            return;

        var active = vm.ActiveEpisode is not null;
        if (_episodeWasActive && !active)
        {
            ScreenFadeOverlay.Opacity = 1;
            _ = App.Current.StageTransition.FadeFromBlackAsync(ScreenFadeOverlay, 900);
            Focus();
        }
        _episodeWasActive = active;
    }

    /// <summary>
    /// Разыгрывает все анимации для текущего узла. Каждый шаг — await на Storyboard через
    /// ICharacterAnimationService/ISceneTransitionService (requirement: не блокирует диспетчер —
    /// await лишь приостанавливает ЭТОТ метод, UI остаётся отзывчивым всё это время).
    /// </summary>
    private async Task HandleCuesAsync(NodeAnimationCues cues)
    {
        var characterAnimation = App.Current.CharacterAnimation;
        var stageTransition = App.Current.StageTransition;
        var assets = ((NovelViewModel)DataContext).Assets;

        // Окно реплики красиво уходит и возвращается на каждом новом узле (не только при первом
        // показе экрана) — не блокирует остальные анимации узла, это независимая панель.
        _ = AnimateDialogueBoxTransitionAsync();

        if (cues.IsSceneEntry)
        {
            ApplyBackgroundLook(cues.Background, assets);

            // Новая сцена — новый эпизод: персонажи предыдущей сцены этой же главы не должны
            // незаметно "унаследоваться" на новый фон, если они не участвуют в новой сцене —
            // каждая сцена вводит своих участников явно, через собственные characterCues (Enter).
            await Task.WhenAll(
                ClearCharactersAsync(characterAnimation),
                CrossfadeBackgroundAsync(stageTransition, cues.BackgroundTransition?.DurationMs ?? 1000));

            stageTransition.StopParallax(BackgroundLayerA);
            stageTransition.StopParallax(BackgroundLayerB);
            if (cues.BackgroundTransition?.EnableParallax == true)
                stageTransition.StartParallax(ActiveBackgroundLayer);

            ApplyAmbientEffect(cues.AmbientEffect);
        }

        // Персонаж виден на экране, только пока говорит именно он (и Макс — на время своего
        // внутреннего монолога, для которого в данных используется тот же speaker "max_orlov"):
        // автоматически убираем всех, кроме текущего говорящего, и выводим его, если он ещё не
        // на сцене. Рассказчик и безликие служебные роли (без записи в characters.json) никого
        // не показывают. Это полностью заменяет ручные characterCues узла как источник
        // присутствия — сценарные данные их больше не используют для этой цели.
        await ApplySpeakerVisibilityAsync(characterAnimation, assets, cues.Speaker);

        if (cues.CameraCue is { } camera)
        {
            var targetScale = camera.Kind == CameraCueKind.Reset ? 1.0 : camera.Amount;
            _ = stageTransition.ZoomAsync(StageRoot, targetScale, camera.DurationMs); // не держим текст ради камеры
        }

        if (!_hasPlayedOpeningReveal)
        {
            // Первый показ сцены вообще — классическое "из темноты" открытие, а не обязательная
            // реплика сценария: экран стартует чёрным (см. XAML) именно для этого кадра.
            _hasPlayedOpeningReveal = true;
            await stageTransition.FadeFromBlackAsync(ScreenFadeOverlay, 900);
        }
        else if (cues.ScreenFadeCue is { } fade)
        {
            if (fade.Kind == ScreenFadeKind.FadeToBlack)
                await stageTransition.FadeToBlackAsync(ScreenFadeOverlay, fade.DurationMs);
            else
                await stageTransition.FadeFromBlackAsync(ScreenFadeOverlay, fade.DurationMs);
        }
    }

    /// <summary>Лёгкий уход старой реплики и приход новой при каждом показанном узле — окно
    /// реплики не мгновенно подменяет текст, а читается как уход и возвращение панели.</summary>
    private async Task AnimateDialogueBoxTransitionAsync()
    {
        var reveal = App.Current.UiReveal;
        await reveal.HideAsync(DialogueBox, 150);
        await reveal.RevealAsync(DialogueBox, 320);
    }

    /// <summary>Убирает со сцены всех персонажей, оставшихся от предыдущей сцены этой же главы —
    /// каждый с собственной анимацией исчезновения (requirement: "не должны быть видимыми, если
    /// не участвуют" — применяется на границе сцен; внутри одной сцены участники заданы явно
    /// через characterCues конкретных узлов).</summary>
    private async Task ClearCharactersAsync(ICharacterAnimationService service)
    {
        var presentIds = CharacterLayer.PresentCharacterIds;
        if (presentIds.Count == 0)
            return;

        await Task.WhenAll(presentIds.Select(id => ExitAndRemoveAsync(service, id)));
    }

    private async Task ExitAndRemoveAsync(ICharacterAnimationService service, string characterId)
    {
        var element = CharacterLayer.GetCharacterIfPresent(characterId);
        if (element is null)
            return;

        service.StopIdleBob(element);
        await service.ExitAsync(element, 450);
        CharacterLayer.RemoveCharacter(characterId);
    }

    /// <summary>
    /// Автоматическая видимость персонажей по говорящему текущего узла — персонаж появляется,
    /// только пока говорит именно он. Если говорящий не зарегистрирован как персонаж (рассказчик,
    /// безликий служебный голос без записи в characters.json), не показывается никто. Тот же
    /// говорящий два узла подряд остаётся на сцене без повторного появления. Позиция на сцене —
    /// CharacterDefinition.DefaultPosition (characters.json), не CharacterCue.Position узла: по
    /// требованию сценариста Макс всегда слева от центра, остальные персонажи — на столько же
    /// правее центра.
    /// </summary>
    private async Task ApplySpeakerVisibilityAsync(ICharacterAnimationService service, IAssetService assets, string speakerId)
    {
        var desiredCharacterId = assets.GetCharacterDefinition(speakerId) is not null ? speakerId : null;

        var toExit = CharacterLayer.PresentCharacterIds.Where(id => id != desiredCharacterId).ToList();
        if (toExit.Count > 0)
            await Task.WhenAll(toExit.Select(id => ExitAndRemoveAsync(service, id)));

        if (desiredCharacterId is null || CharacterLayer.GetCharacterIfPresent(desiredCharacterId) is not null)
            return;

        var definition = assets.GetCharacterDefinition(desiredCharacterId);
        var defaultScale = definition?.DefaultScale ?? 1.0;
        var element = CharacterLayer.GetOrCreateCharacter(desiredCharacterId, assets);
        var x = StageLayout.ResolveX(definition?.DefaultPosition ?? StagePosition.Center);
        await service.EnterAsync(element, x, 0, defaultScale, 500);
        service.StartIdleBob(element);
    }

    /// <summary>
    /// Реальный фон (requirement "подключить изображения к системе локаций") приоритетнее
    /// демо-градиентов: если backgroundId зарегистрирован в Data/Backgrounds/backgrounds.json
    /// и файл читается — показывается он (UniformToFill: сохраняет пропорции и заполняет кадр
    /// без искажений, с обрезкой краёв при несовпадении соотношения сторон — требование
    /// "не допускай растягивания", не требование "не обрезай", в отличие от персонажей).
    /// Иначе — старое поведение демо-сценария (градиент-заглушка + подпись id), без изменений.
    /// </summary>
    private void ApplyBackgroundLook(string? backgroundId, IAssetService assets)
    {
        var incomingLayer = _isLayerAActive ? BackgroundLayerB : BackgroundLayerA;
        var realImage = backgroundId is not null ? assets.ResolveBackgroundImage(backgroundId) : null;

        if (realImage is not null)
        {
            incomingLayer.Background = new ImageBrush(realImage) { Stretch = Stretch.UniformToFill };
            BackgroundCaption.Text = string.Empty;
            ApplyDialogueBoxSceneTint(ComputeAverageColor(realImage));
            return;
        }

        var brush = backgroundId is not null && DemoBackgrounds.TryGetValue(backgroundId, out var found)
            ? found
            : DemoBackgrounds["demo_bg_dusk"];

        incomingLayer.Background = brush;
        ApplyDialogueBoxSceneTint(DefaultPanelTintColor);

        BackgroundCaption.Text = backgroundId is null
            ? "Фон сцены не загружен (демо)"
            : $"Фон сцены: {backgroundId} (изображение не загружено)";
    }

    // База смешения — тот же тон, что раньше был фиксированным цветом окна реплики, чтобы при
    // отсутствии реального фона (демо-градиент) вид не отличался от прежнего по умолчанию.
    private static readonly Color DefaultPanelTintColor = Color.FromRgb(0x15, 0x1A, 0x26);

    /// <summary>
    /// Окно реплики красится в цвета текущего фона, а не в один фиксированный цвет для любой
    /// сцены: берём средний цвет изображения фона и смешиваем его с тёмной базой (чтобы белый
    /// текст поверх оставался читаемым на любом, даже светлом, фоне), сохраняя прежний
    /// вертикальный градиент "чуть светлее сверху, плотнее снизу".
    /// </summary>
    private void ApplyDialogueBoxSceneTint(Color sceneAverage)
    {
        const double topSceneWeight = 0.45;
        const double bottomSceneWeight = 0.30;

        var top = BlendTowardBase(sceneAverage, DefaultPanelTintColor, topSceneWeight, alpha: 0xA8);
        var bottom = BlendTowardBase(sceneAverage, DefaultPanelTintColor, bottomSceneWeight, alpha: 0xE6);

        DialogueBox.SetSceneTint(top, bottom);
    }

    private static Color BlendTowardBase(Color scene, Color baseColor, double sceneWeight, byte alpha)
    {
        byte Blend(byte sceneChannel, byte baseChannel) =>
            (byte)Math.Clamp(sceneChannel * sceneWeight + baseChannel * (1 - sceneWeight), 0, 255);

        return Color.FromArgb(alpha, Blend(scene.R, baseColor.R), Blend(scene.G, baseColor.G), Blend(scene.B, baseColor.B));
    }

    /// <summary>
    /// Средний цвет изображения — рендерит его уменьшенную копию (дешёвая, один раз на смену
    /// сцены, не на каждый кадр) и усредняет пиксели. Стандартный WPF-приём для "преобладающего
    /// цвета", не требующий сторонних библиотек.
    /// </summary>
    private static Color ComputeAverageColor(BitmapSource source)
    {
        const int sampleSize = 12;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(source, new Rect(0, 0, sampleSize, sampleSize));

        var thumbnail = new RenderTargetBitmap(sampleSize, sampleSize, 96, 96, PixelFormats.Pbgra32);
        thumbnail.Render(visual);

        var pixels = new byte[sampleSize * sampleSize * 4];
        thumbnail.CopyPixels(pixels, sampleSize * 4, 0);

        long b = 0, g = 0, r = 0;
        var pixelCount = sampleSize * sampleSize;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
        }

        return Color.FromRgb((byte)(r / pixelCount), (byte)(g / pixelCount), (byte)(b / pixelCount));
    }

    private async Task CrossfadeBackgroundAsync(ISceneTransitionService service, int durationMs)
    {
        var hidingLayer = _isLayerAActive ? BackgroundLayerA : BackgroundLayerB;
        var revealingLayer = _isLayerAActive ? BackgroundLayerB : BackgroundLayerA;

        await service.CrossfadeAsync(hidingLayer, revealingLayer, durationMs);
        _isLayerAActive = !_isLayerAActive;
    }

    private FrameworkElement ActiveBackgroundLayer => _isLayerAActive ? BackgroundLayerA : BackgroundLayerB;

    private void ApplyAmbientEffect(AmbientEffectKind kind)
    {
        AmbientLayer.Children.Clear();

        var particleCount = kind switch
        {
            AmbientEffectKind.Dust => 10,
            AmbientEffectKind.Haze => 4,
            AmbientEffectKind.Flicker => 1,
            _ => 0
        };

        for (var i = 0; i < particleCount; i++)
            AmbientLayer.Children.Add(CreateAmbientParticle(kind));
    }

    private static UIElement CreateAmbientParticle(AmbientEffectKind kind)
    {
        if (kind == AmbientEffectKind.Flicker)
            return CreateFlickerOverlay();

        var isHaze = kind == AmbientEffectKind.Haze;
        var size = isHaze ? Rng.Next(220, 420) : Rng.Next(2, 5);
        var targetOpacity = isHaze ? 0.05 : 0.12;

        var particle = new Ellipse { Width = size, Height = size, Fill = Brushes.White, Opacity = 0 };
        Canvas.SetLeft(particle, Rng.NextDouble() * 1920);
        Canvas.SetTop(particle, Rng.NextDouble() * 1080);

        var translate = new TranslateTransform();
        particle.RenderTransform = translate;

        var driftDuration = TimeSpan.FromSeconds(10 + Rng.NextDouble() * 10);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 0,
            To = -40 - Rng.Next(0, 60),
            Duration = driftDuration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        });

        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = (Rng.NextDouble() - 0.5) * 40,
            Duration = driftDuration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease
        });

        particle.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, targetOpacity, TimeSpan.FromSeconds(2)));

        return particle;
    }

    private static UIElement CreateFlickerOverlay()
    {
        var overlay = new Border { Background = Brushes.White, Opacity = 0, Width = 1920, Height = 1080 };
        Canvas.SetLeft(overlay, 0);
        Canvas.SetTop(overlay, 0);

        overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 0.04,
            Duration = TimeSpan.FromSeconds(0.2 + Rng.NextDouble() * 0.3),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });

        return overlay;
    }

    private static LinearGradientBrush CreateGradient(string fromHex, string toHex)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(fromHex), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(toHex), 1));
        brush.Freeze();
        return brush;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Focus();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not NovelViewModel viewModel)
            return;

        if (viewModel.ActiveEpisode is not null)
        {
            e.Handled = IllustratedEpisodeOverlay.HandleKey(e);
            return;
        }

        if (viewModel.ActiveArchiveRecordVerification is not null ||
            viewModel.ActiveMarketWitnessRoute is not null || viewModel.ActiveMarketCardGame is not null)
            return;

        if (viewModel.ActiveBirthFloorComparison is { } comparison)
        {
            switch (e.Key)
            {
                case Key.Left:
                    Invoke(comparison.SelectLowerFloorCommand);
                    e.Handled = true;
                    break;
                case Key.Right:
                    Invoke(comparison.SelectMiddleFloorCommand);
                    e.Handled = true;
                    break;
                case Key.Enter or Key.Space when e.OriginalSource is not Button:
                    if (!e.IsRepeat)
                        Invoke(comparison.ContinueCommand);
                    e.Handled = true;
                    break;
            }
            return;
        }

        // Мини-игра «Синхронизация слепых зон» (Глава 6, StealthGrid) перехватывает стрелки/WASD
        // для движения по сетке — пока она активна, обычная навигация по списку выбора (которой
        // в этот момент и так нет на экране, см. IsChoiceVisible) не нужна.
        if (viewModel.ActiveStealthGrid is { } stealthGrid)
        {
            switch (e.Key)
            {
                case Key.Up or Key.W:
                    Invoke(stealthGrid.MoveUpCommand);
                    e.Handled = true;
                    return;

                case Key.Down or Key.S:
                    Invoke(stealthGrid.MoveDownCommand);
                    e.Handled = true;
                    return;

                case Key.Left or Key.A:
                    Invoke(stealthGrid.MoveLeftCommand);
                    e.Handled = true;
                    return;

                case Key.Right or Key.D:
                    Invoke(stealthGrid.MoveRightCommand);
                    e.Handled = true;
                    return;
            }
        }

        // «Кража обходчика» (Глава 2, TimingBar): Пробел/Enter — то же, что кнопка «Спрятать чип»,
        // ровно один раз за нажатие.
        if (viewModel.ActiveTimingBarMinigame is { } timingBar && e.Key is Key.Space or Key.Enter)
        {
            if (!e.IsRepeat)
                Invoke(timingBar.PressCommand);
            e.Handled = true;
            return;
        }

        // Мини-игра «Диагностика троса» (Глава 1, CableDiagnostics) — держим Space, чтобы качать
        // насос; StartPumping идемпотентен, поэтому авто-повтор KeyDown от удержания клавиши
        // безопасен. Остановка — по PreviewKeyUp (см. OnPreviewKeyUp), не здесь.
        if (viewModel.ActiveCableDiagnostics is { } cableDiagnostics && e.Key == Key.Space)
        {
            cableDiagnostics.StartPumping();
            e.Handled = true;
            return;
        }

        // Мини-игра «Калибровка обходчика» (Глава 3, RingLock) — держим стрелки, чтобы вращать
        // внутреннее кольцо (авто-повтор от удержания безопасен, как и у насоса выше); Пробел —
        // подтвердить совмещение РОВНО один раз за нажатие (!e.IsRepeat), а не на каждый
        // авто-повтор, пока клавиша зажата.
        if (viewModel.ActiveRingLock is { } ringLock)
        {
            switch (e.Key)
            {
                case Key.Left:
                    ringLock.StartRotatingLeft();
                    e.Handled = true;
                    return;

                case Key.Right:
                    ringLock.StartRotatingRight();
                    e.Handled = true;
                    return;

                case Key.Space:
                    if (!e.IsRepeat)
                        Invoke(ringLock.ConfirmCommand);
                    e.Handled = true;
                    return;
            }
        }

        // Мини-игра «Скан лога панели» (Глава 3, LogScan) — Enter/Space "ловит" текущую активную
        // строку ленты; тоже ровно один раз за нажатие, не на авто-повтор.
        if (viewModel.ActiveLogScan is { } logScan && e.Key is Key.Enter or Key.Space)
        {
            if (!e.IsRepeat)
                Invoke(logScan.ConfirmCommand);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                Invoke(viewModel.SelectPreviousChoiceCommand);
                e.Handled = true;
                break;

            case Key.Down:
                Invoke(viewModel.SelectNextChoiceCommand);
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Space:
                if (viewModel.IsChoiceVisible)
                    Invoke(viewModel.ConfirmSelectedChoiceCommand);
                else
                    Invoke(viewModel.ContinueCommand);
                e.Handled = true;
                break;

            case Key.Escape:
                if (viewModel.IsPauseMenuVisible)
                    Invoke(viewModel.PauseMenu.ResumeCommand);
                else if (viewModel.IsLogVisible)
                    Invoke(viewModel.ToggleLogCommand);
                else
                    Invoke(viewModel.OpenPauseMenuCommand);
                e.Handled = true;
                break;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (DataContext is not NovelViewModel viewModel)
            return;

        if (viewModel.ActiveCableDiagnostics is { } cableDiagnostics && e.Key == Key.Space)
        {
            cableDiagnostics.StopPumping();
            e.Handled = true;
        }

        if (viewModel.ActiveRingLock is { } ringLock && e.Key is Key.Left or Key.Right)
        {
            ringLock.StopRotating();
            e.Handled = true;
        }
    }

    private static void Invoke(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }
}
