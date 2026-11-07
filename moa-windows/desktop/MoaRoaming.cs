using System;
using System.Drawing;

namespace Moa
{
    // Pixel coordinates may be negative on a monitor to the left of the primary display.
    public sealed class MoaRoaming
    {
        private readonly Random random;
        private double x, y, wait;
        private PointF target;
        private bool hasTarget;
        public bool Enabled = true;
        public bool Moving { get; private set; }
        public bool FacingLeft { get; private set; }
        public double DistanceTravelled { get; private set; }
        public double Speed { get; private set; }
        public double Heading { get; private set; }
        public Point Position { get { return new Point((int)Math.Round(x), (int)Math.Round(y)); } }
        public MoaRoaming(Point start, int seed) { x = start.X; y = start.Y; random = new Random(seed); }
        public static Point Clamp(Point point, Rectangle area, Size size)
        {
            return new Point(Math.Max(area.Left, Math.Min(point.X, Math.Max(area.Left, area.Right - size.Width))),
                Math.Max(area.Top, Math.Min(point.Y, Math.Max(area.Top, area.Bottom - size.Height))));
        }
        public void Place(Point point, Rectangle area, Size size)
        {
            point = Clamp(point, area, size); x = point.X; y = point.Y; hasTarget = false; wait = 0; Moving = false; Speed = 0;
        }
        public Point Step(double seconds, Rectangle area, Size size, bool paused)
        {
            Point clamped = Clamp(Position, area, size);
            if (clamped != Position) Place(clamped, area, size);
            Moving = false;
            if (!Enabled || paused) { Speed = 0; return Position; }
            seconds = Math.Max(0, Math.Min(seconds, .1));
            if (wait > 0) { wait -= seconds; return Position; }
            if (!hasTarget)
            {
                int right = Math.Max(area.Left, area.Right - size.Width), bottom = Math.Max(area.Top, area.Bottom - size.Height);
                target = new PointF(random.Next(area.Left, right + 1), random.Next(area.Top, bottom + 1));
                // Begin visibly walking rather than choosing a point beside the starting position.
                if (Math.Abs(target.X - x) < 120 && right - area.Left > 240)
                    target.X = x > (area.Left + right) / 2.0 ? area.Left + 30 : right - 30;
                hasTarget = true;
            }
            Point boundedTarget = Clamp(Point.Round(target), area, size);
            target = boundedTarget;
            double dx = target.X - x, dy = target.Y - y, distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < 1) { hasTarget = false; Speed = 0; wait = .7 + random.NextDouble(); return Position; }
            double desired = Math.Min(58, Math.Sqrt(2 * 110 * distance));
            Speed += Math.Max(-140 * seconds, Math.Min(110 * seconds, desired - Speed));
            double travel = Math.Min(distance, Speed * seconds);
            x += dx / distance * travel; y += dy / distance * travel;
            if (Math.Abs(dx) > 1) FacingLeft = dx < 0;
            Heading = Math.Atan2(dy, Math.Abs(dx));
            DistanceTravelled += travel;
            Moving = travel > 0;
            return Clamp(Position, area, size);
        }
    }
}
