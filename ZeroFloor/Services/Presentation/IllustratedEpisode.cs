using System.Windows;
using ZeroFloor.Models;

namespace ZeroFloor.Services.Presentation;

internal sealed class IllustratedEpisode
{
    internal sealed record Exit(Point Position, string Label, string Route);
    internal sealed record Scanner(Point Origin, double Bearing, double Sweep, double Period, double Range, double Phase, double Drift = 0)
    {
        public Point OriginAt(double time) =>
            Origin + new Vector(Drift * Math.Sin(time * Math.PI / Period + Phase), 0);

        public double AngleAt(double time) => Bearing + Sweep * Math.Sin(time * 2 * Math.PI / Period + Phase);
    }

    private const double BeamHalfAngle = 0.22;
    private const double ExitReach = 54;
    private readonly double _safeTime;

    public IllustratedEpisode(MinigameKind kind)
    {
        Kind = kind;
        switch (kind)
        {
            case MinigameKind.MarketStealth3D:
                Background = "market_floor.jpg";
                Location = "РЫНОК  /  ПОДХОД К ПРИЛАВКУ";
                Instruction = "Обойдите прожектор через нижний ряд или пройдите напрямую. У выхода — E.";
                Floor = new Rect(135, 401, 1025, 231);
                Position = new Point(158, 504);
                Cover = [new(425, 520, 118, 54), new(756, 533, 132, 56)];
                Scanners = [new(new(643, 323), 1.58, 1.05, 5.9, 265, 0, 48)];
                Exits = [new(new(1058, 420), "К Гоше", "")];
                break;
            case MinigameKind.ShaftsChase3D:
                Background = "shafts_floor.jpg";
                Location = "ТЕХНИЧЕСКИЕ СТВОЛЫ  /  140–160";
                Instruction = "Патруль приближается. Удерживайте Shift для рывка и выберите выход.";
                Floor = new Rect(75, 454, 1125, 178);
                Position = new Point(111, 480);
                Cover = [new(396, 536, 123, 54), new(720, 459, 110, 49)];
                Scanners = [new(new(375, 328), 1.5, 0.65, 4.7, 238, 0.6, 24),
                    new(new(891, 329), 1.7, 0.7, 5.1, 253, 2, 32)];
                Exits = [new(new(1150, 463), "Жилой сектор", "residential"),
                    new(new(1131, 596), "Вентиляция", "ventilation")];
                _safeTime = 9.5;
                break;
            default:
                Background = "archive_floor.jpg";
                Location = "АРХИВ  /  110-Й ЭТАЖ";
                Instruction = "Следите за двумя камерами. Доберитесь до терминала между проходами лучей.";
                Floor = new Rect(124, 397, 1012, 241);
                Position = new Point(145, 575);
                Cover = [new(375, 507, 124, 53), new(694, 473, 115, 56), new(953, 538, 104, 56)];
                Scanners = [new(new(475, 329), 1.48, 0.82, 5.2, 314, 0),
                    new(new(916, 329), 1.7, 0.87, 6.4, 310, 2.4)];
                Exits = [new(new(1122, 450), "Архивный терминал", "")];
                break;
        }
    }

    public MinigameKind Kind { get; }
    public string Background { get; }
    public string Location { get; }
    public string Instruction { get; }
    public Rect Floor { get; }
    public IReadOnlyList<Exit> Exits { get; }
    public IReadOnlyList<Scanner> Scanners { get; }
    public IReadOnlyList<Rect> Cover { get; }
    public Point Position { get; private set; }
    public double Time { get; private set; }
    public double Exposure { get; private set; }
    public bool Detected { get; private set; }
    public bool Moving { get; private set; }
    public bool FacingRight { get; private set; } = true;
    public bool IsChase => Kind == MinigameKind.ShaftsChase3D;
    public double Pursuit => IsChase ? Math.Clamp((Time / _safeTime) * 0.55 + _alerts * 0.22, 0, 1) : 0;
    public string MarketRoute { get; private set; } = "direct";
    public Exit? NearbyExit => Exits.FirstOrDefault(exit => InReach(exit, Position));
    private int _alerts;
    private bool _scanLatched;

    public static bool InReach(Exit exit, Point position) => (exit.Position - position).Length <= ExitReach;

    public void Step(double dt, Vector direction, bool sprint)
    {
        Time += dt;
        if (direction.Length > 1)
            direction.Normalize();
        var previous = Position;
        var delta = direction * (IsChase && sprint ? 250 : 173) * dt;
        Position = Walkable(new Point(previous.X + delta.X, previous.Y), previous);
        Position = Walkable(new Point(Position.X, Position.Y + delta.Y * 0.72), Position);
        Moving = (Position - previous).Length > 0.05;
        if (Moving && Math.Abs(delta.X) > 0.05)
            FacingRight = delta.X > 0;
        if (Kind == MinigameKind.MarketStealth3D && previous.X <= 647 && Position.X > 647)
            MarketRoute = Position.Y > 550 ? "detour" : "direct";

        var scanned = Scanners.Any(IsInBeam);
        Exposure = Math.Clamp(Exposure + dt * (scanned ? 1.65 : -0.85), 0, 1);
        if (!_scanLatched && Exposure >= 1)
        {
            _scanLatched = true;
            Detected = true;
            _alerts++;
        }
        else if (Exposure < 0.2)
        {
            _scanLatched = false;
        }
    }

    public string Outcome => Kind switch
    {
        MinigameKind.MarketStealth3D => Detected ? "DETECTED" : "CLEAN",
        MinigameKind.ArchiveStealth3D => Detected ? "SPOTTED" : "CLEAN",
        _ => Pursuit >= 0.96 ? "ROUGH" : _alerts == 0 && Time <= _safeTime ? "PERFECT" : "NORMAL"
    };

    public Point BeamEnd(Scanner scanner, double angle)
    {
        var origin = scanner.OriginAt(Time);
        var ray = new Vector(Math.Cos(angle), Math.Sin(angle));
        var limit = scanner.Range;
        foreach (var cover in Cover)
        {
            // Sample the intersection with each edge of the rectangular cover.
            ClipRayAtEdge(cover.Left, cover.Top, cover.Bottom, origin.X, origin.Y, ray.X, ray.Y, ref limit);
            ClipRayAtEdge(cover.Right, cover.Top, cover.Bottom, origin.X, origin.Y, ray.X, ray.Y, ref limit);
            ClipRayAtEdge(cover.Top, cover.Left, cover.Right, origin.Y, origin.X, ray.Y, ray.X, ref limit);
            ClipRayAtEdge(cover.Bottom, cover.Left, cover.Right, origin.Y, origin.X, ray.Y, ray.X, ref limit);
        }
        return origin + ray * limit;
    }

    public double BeamAngle(Scanner scanner, double fraction) =>
        scanner.AngleAt(Time) + (fraction * 2 - 1) * BeamHalfAngle;

    private Point Walkable(Point candidate, Point previous)
    {
        candidate = new Point(Math.Clamp(candidate.X, Floor.Left, Floor.Right), Math.Clamp(candidate.Y, Floor.Top, Floor.Bottom));
        foreach (var cover in Cover)
        {
            var collision = cover;
            collision.Inflate(9, 6);
            if (collision.Contains(candidate))
                return previous;
        }
        return candidate;
    }

    private bool IsInBeam(Scanner scanner)
    {
        var origin = scanner.OriginAt(Time);
        var difference = Position - origin;
        var angle = Math.Atan2(difference.Y, difference.X);
        if (Math.Abs(angle - scanner.AngleAt(Time)) > BeamHalfAngle)
            return false;
        return difference.Length <= (BeamEnd(scanner, angle) - origin).Length;
    }

    private static void ClipRayAtEdge(double edge, double min, double max, double primary, double secondary,
        double direction, double crossDirection, ref double limit)
    {
        if (Math.Abs(direction) < 0.0001)
            return;
        var distance = (edge - primary) / direction;
        var intersection = secondary + crossDirection * distance;
        if (distance >= 0 && distance < limit && intersection >= min && intersection <= max)
            limit = distance;
    }
}
