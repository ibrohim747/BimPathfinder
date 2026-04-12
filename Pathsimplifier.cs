using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace BimPathfinder
{
    internal static class PathSimplifier
    {
        private const double CollinearTolerance = 1e-6;

        public static List<XYZ> Simplify(List<XYZ> path)
        {
            if (path == null || path.Count <= 2)
                return path ?? new List<XYZ>();

            var result = new List<XYZ> { path[0] };

            for (int i = 1; i < path.Count - 1; i++)
            {
                XYZ prev = result[result.Count - 1];
                XYZ current = path[i];
                XYZ next = path[i + 1];

                if (!AreCollinear(prev, current, next))
                    result.Add(current);
            }

            result.Add(path[path.Count - 1]);
            return result;
        }

        public static List<XYZ> RemoveZigzags(List<XYZ> path, double stepFt)
        {
            if (path.Count <= 3) return path;

            bool changed = true;
            while (changed)
            {
                changed = false;
                var result = new List<XYZ> { path[0] };

                for (int i = 1; i < path.Count - 1; i++)
                {
                    XYZ a = result[result.Count - 1];
                    XYZ b = path[i];
                    XYZ c = path[i + 1];

                    XYZ ab = (b - a);
                    XYZ bc = (c - b);

                    bool isOpposite = ab.DotProduct(bc) < -0.99 * ab.GetLength() * bc.GetLength();
                    if (isOpposite && ab.GetLength() <= stepFt * 1.5 &&
                                      bc.GetLength() <= stepFt * 1.5)
                    {
                        changed = true;
                    }
                    else
                    {
                        result.Add(b);
                    }
                }

                result.Add(path[path.Count - 1]);
                path = result;
            }

            return path;
        }

        // ── Check collinearity of three points ───────────────────────
        private static bool AreCollinear(XYZ a, XYZ b, XYZ c)
        {
            XYZ ab = b - a;
            XYZ ac = c - a;

            if (ab.GetLength() < CollinearTolerance) return true;

            XYZ cross = ab.CrossProduct(ac);
            return cross.GetLength() < CollinearTolerance;
        }

        public static List<XYZ> FullSimplify(List<XYZ> raw, double stepFt)
        {
            var step1 = Simplify(raw);
            var step2 = RemoveZigzags(step1, stepFt);
            return Simplify(step2);
        }
    }
}