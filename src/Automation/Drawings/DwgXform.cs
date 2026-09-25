using System;
using ACadSharp.Entities;

namespace SleevesOpenings.Automation.Drawings
{
    /// <summary>2D transform of a nested block insert (insert point, rotation, scale), used to bring block contents into model space.</summary>
    internal struct DwgXform
    {
        public double Ox, Oy, Cos, Sin, Sx, Sy;

        public static DwgXform Identity => new DwgXform { Cos = 1, Sx = 1, Sy = 1 };

        public (double X, double Y) Apply(double x, double y) =>
            (Ox + (x * Sx) * Cos - (y * Sy) * Sin, Oy + (x * Sx) * Sin + (y * Sy) * Cos);

        public DwgXform Then(Insert ins)
        {
            var (ox, oy) = Apply(ins.InsertPoint.X, ins.InsertPoint.Y);
            double a = Math.Atan2(Sin, Cos) + ins.Rotation;
            return new DwgXform { Ox = ox, Oy = oy, Cos = Math.Cos(a), Sin = Math.Sin(a), Sx = Sx * ins.XScale, Sy = Sy * ins.YScale };
        }
    }
}
