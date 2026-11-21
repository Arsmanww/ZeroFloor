using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZeroFloor.Models;
using ZeroFloor.Services.Presentation;

namespace ZeroFloor.Views.Controls;

public sealed class IllustratedEpisodeStage : FrameworkElement
{
    private const double FrameWidth = 1280;
    private const double FrameHeight = 720;
    private static readonly Brush Ink = Brush("#E9101B21");
    private static readonly Brush Mint = Brush("#8CEBD1");
    private static readonly Brush Amber = Brush("#FFC479");
    private static readonly Brush Paper = Brush("#EDF7F4");
    private static readonly Brush Shadow = Brush("#7B051014");
    private static readonly Pen Outline = new(Brush("#617881"), 1);
    private static readonly Pen BeamEdge = new(Brush("#66FFC479"), 1);
    private static readonly Brush Beam = Brush("#37FFC479");
    private static readonly Brush HotBeam = Brush("#5AFF6048");
    private static readonly Typeface LabelFont = new("Segoe UI Semibold");

    private ImageSource? _background;
    private ImageSource? _actor;
    private ImageSource? _crate;
    private IllustratedEpisode? _scene;

    internal void SetScene(IllustratedEpisode? scene)
    {
        _scene = scene;
        if (scene is null)
        {
            _background = null;
            _actor = null;
            _crate = null;
        }
        else
        {
            var fallback = scene.Kind switch
            {
                MinigameKind.MarketStealth3D => "shadow_market_approach.png",
                MinigameKind.ShaftsChase3D => "technical_shafts.jpg",
                _ => "keepers_archive.jpg"
            };
            _background = Load("Episodes", scene.Background) ?? Load("Backgrounds", fallback);
            _actor = Load("Episodes", "max_walk.png") ?? Load("Characters", "max_orlov.png");
            _crate = Load("Episodes", "equipment_crate.png");
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var frame = new Rect(0, 0, FrameWidth, FrameHeight);
        dc.DrawRectangle(Ink, null, frame);
        if (_scene is not { } scene)
            return;

        if (_background is not null)
            dc.DrawImage(_background, frame);
        foreach (var scanner in scene.Scanners)
            DrawBeam(dc, scene, scanner);

        foreach (var exit in scene.Exits)
            DrawExit(dc, scene, exit);

        // The player's feet determine which side of the cover is visible.
        foreach (var cover in scene.Cover.Where(c => c.Bottom <= scene.Position.Y))
            DrawCover(dc, cover);
        DrawPlayer(dc, scene);
        foreach (var cover in scene.Cover.Where(c => c.Bottom > scene.Position.Y))
            DrawCover(dc, cover);
    }

    private void DrawBeam(DrawingContext dc, IllustratedEpisode scene, IllustratedEpisode.Scanner scanner)
    {
        var origin = scanner.OriginAt(scene.Time);
        var shape = new StreamGeometry();
        using (var geometry = shape.Open())
        {
            geometry.BeginFigure(origin, true, true);
            for (var i = 0; i <= 24; i++)
                geometry.LineTo(scene.BeamEnd(scanner, scene.BeamAngle(scanner, i / 24.0)), true, false);
        }
        dc.DrawGeometry(scene.Exposure > 0.4 ? HotBeam : Beam, BeamEdge, shape);
        if (scanner.Drift > 0)
        {
            dc.DrawEllipse(Ink, Outline, new Point(origin.X - 16, origin.Y - 7), 8, 3);
            dc.DrawEllipse(Ink, Outline, new Point(origin.X + 16, origin.Y - 7), 8, 3);
        }
        dc.DrawRoundedRectangle(Ink, Outline, new Rect(origin.X - 15, origin.Y - 8, 30, 14), 3, 3);
        dc.DrawEllipse(scene.Exposure > 0.4 ? Amber : Mint, null, origin, 4, 4);
    }

    private void DrawExit(DrawingContext dc, IllustratedEpisode scene, IllustratedEpisode.Exit exit)
    {
        var p = exit.Position;
        var near = IllustratedEpisode.InReach(exit, scene.Position);
        var accent = exit.Route == "residential" ? Amber : Mint;
        var marker = new Pen(accent, near ? 3 : 1.5);
        if (exit.Route == "ventilation")
        {
            var hatch = new Rect(p.X - 42, p.Y - 16, 84, 40);
            dc.DrawRoundedRectangle(Ink, Outline, hatch, 3, 3);
            for (var x = hatch.Left + 9; x < hatch.Right - 5; x += 8)
                dc.DrawLine(Outline, new Point(x, hatch.Top + 5), new Point(x - 3, hatch.Bottom - 5));
        }
        dc.DrawEllipse(Shadow, marker, p, near ? 25 : 17, near ? 11 : 8);
        dc.DrawLine(marker, new Point(p.X, p.Y - 7), new Point(p.X, p.Y - 24));
        var title = Label(near ? $"E   {exit.Label}" : exit.Label, 16, near ? Paper : accent);
        var rect = new Rect(p.X - title.Width / 2 - 11, p.Y - 52, title.Width + 22, 27);
        dc.DrawRoundedRectangle(Ink, Outline, rect, 3, 3);
        dc.DrawText(title, new Point(rect.X + 11, rect.Y + 3));
    }

    private void DrawCover(DrawingContext dc, Rect foot)
    {
        var front = new Rect(foot.Left, foot.Top - 5, foot.Width, foot.Height + 5);
        dc.DrawEllipse(Shadow, null, new Point(foot.Left + foot.Width / 2, foot.Bottom), foot.Width * 0.58, 11);
        if (_crate is not null)
        {
            var height = foot.Width * _crate.Height / _crate.Width;
            dc.DrawImage(_crate, new Rect(foot.Left, foot.Bottom - height, foot.Width, height));
            return;
        }
        dc.DrawRoundedRectangle(Brush("#25373B"), Outline, front, 3, 3);
        dc.DrawRoundedRectangle(Brush("#45595B"), Outline, new Rect(foot.X + 3, foot.Y - 9, foot.Width - 6, 16), 3, 3);
        dc.DrawRectangle(Brush("#172426"), Outline, new Rect(foot.X + 13, foot.Y + 15, foot.Width - 26, foot.Height - 23));
        for (var x = foot.X + 28; x < foot.Right - 12; x += 18)
            dc.DrawLine(Outline, new Point(x, foot.Y + 22), new Point(x, foot.Bottom - 14));
        dc.DrawRectangle(Amber, null, new Rect(foot.X + 6, foot.Y + 16, 3, 15));
    }

    private void DrawPlayer(DrawingContext dc, IllustratedEpisode scene)
    {
        var p = scene.Position;
        var depth = (p.Y - scene.Floor.Top) / scene.Floor.Height;
        var height = 82 + depth * 42;
        var bob = scene.Moving ? Math.Abs(Math.Sin(scene.Time * 14)) * 4 : 0;
        dc.DrawEllipse(Shadow, null, new Point(p.X, p.Y + 1), 14 + depth * 9, 5 + depth * 3);
        dc.DrawEllipse(null, new Pen(scene.Exposure > 0.4 ? Amber : Mint, 1.25), p, 16 + depth * 6, 6);
        if (_actor is not null)
        {
            var width = height * _actor.Width / _actor.Height;
            if (!scene.FacingRight)
                dc.PushTransform(new ScaleTransform(-1, 1, p.X, p.Y));
            dc.DrawImage(_actor, new Rect(p.X - width / 2, p.Y - height - bob, width, height));
            if (!scene.FacingRight)
                dc.Pop();
        }
        var name = Label("МАКС", 11, Paper);
        dc.DrawRoundedRectangle(Ink, null, new Rect(p.X - name.Width / 2 - 5, p.Y + 9, name.Width + 10, 16), 2, 2);
        dc.DrawText(name, new Point(p.X - name.Width / 2, p.Y + 9));
    }

    private FormattedText Label(string text, double size, Brush color) =>
        new(text, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, LabelFont, size, color,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static ImageSource? Load(string folder, string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", folder, name);
        if (!File.Exists(path))
            return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
