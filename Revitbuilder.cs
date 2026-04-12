using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace BimPathfinder
{
    public enum MepSystemType { Pipe, Duct }

    internal sealed class RevitBuilder
    {
        private readonly Document _doc;
        private readonly Level _level;

        public RevitBuilder(Document doc, Level level)
        {
            _doc = doc;
            _level = level;
        }

        // ─────────────────────────────────────────────────────────────
        // PIPE
        // ─────────────────────────────────────────────────────────────

        public List<Pipe> BuildPipes(
            List<XYZ> pathPts,
            PipeType pipeType,
            PipingSystemType systemType,
            double diameterMm)
        {
            if (pathPts == null || pathPts.Count < 2)
                throw new ArgumentException("The path contains fewer than 2 points.");

            double diamFt = diameterMm / 304.8;
            var pipes = new List<Pipe>();

            // ── Step 1: create all segments XYZ → XYZ ─────────────────
            for (int i = 0; i < pathPts.Count - 1; i++)
            {
                XYZ start = pathPts[i];
                XYZ end = pathPts[i + 1];

                if (start.DistanceTo(end) < 1e-6) continue;

                var pipe = Pipe.Create(
                    _doc, systemType.Id, pipeType.Id, _level.Id, start, end);

                pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    ?.Set(diamFt);

                pipes.Add(pipe);
            }

            // ── Step 2: explicitly create Elbow fittings between adjacent segments ──
            InsertPipeElbows(pipes);

            return pipes;
        }

        /// <summary>
        /// Creates elbow fittings via doc.Create.NewElbowFitting(c1, c2).
        /// This is the only reliable approach — independent of Routing Preferences.
        /// </summary>
        private void InsertPipeElbows(List<Pipe> pipes)
        {
            for (int i = 0; i < pipes.Count - 1; i++)
            {
                // Collinear segments — no fitting needed
                if (AreCollinear(pipes[i], pipes[i + 1])) continue;

                Connector c1 = GetConnectorNear(pipes[i], GetEndPoint(pipes[i]));
                Connector c2 = GetConnectorNear(pipes[i + 1], GetStartPoint(pipes[i + 1]));

                if (c1 == null || c2 == null) continue;

                try
                {
                    _doc.Create.NewElbowFitting(c1, c2);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BimPathfinder] Pipe elbow [{i}]: {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // DUCT
        // ─────────────────────────────────────────────────────────────

        public List<Duct> BuildDucts(
            List<XYZ> pathPts,
            DuctType ductType,
            MechanicalSystemType systemType,
            double widthMm,
            double heightMm)
        {
            if (pathPts == null || pathPts.Count < 2)
                throw new ArgumentException("The path contains fewer than 2 points.");

            double wFt = widthMm / 304.8;
            double hFt = heightMm / 304.8;
            var ducts = new List<Duct>();

            // ── Step 1: create all segments XYZ → XYZ ─────────────────
            for (int i = 0; i < pathPts.Count - 1; i++)
            {
                XYZ start = pathPts[i];
                XYZ end = pathPts[i + 1];

                if (start.DistanceTo(end) < 1e-6) continue;

                var duct = Duct.Create(
                    _doc, systemType.Id, ductType.Id, _level.Id, start, end);

                duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(wFt);
                duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(hFt);

                ducts.Add(duct);
            }

            // ── Step 2: explicitly create Elbow fittings ──────────────
            InsertDuctElbows(ducts);

            return ducts;
        }

        private void InsertDuctElbows(List<Duct> ducts)
        {
            for (int i = 0; i < ducts.Count - 1; i++)
            {
                if (AreCollinear(ducts[i], ducts[i + 1])) continue;

                Connector c1 = GetConnectorNear(ducts[i], GetEndPoint(ducts[i]));
                Connector c2 = GetConnectorNear(ducts[i + 1], GetStartPoint(ducts[i + 1]));

                if (c1 == null || c2 == null) continue;

                try
                {
                    _doc.Create.NewElbowFitting(c1, c2);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BimPathfinder] Duct elbow [{i}]: {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Geometry helpers
        // ─────────────────────────────────────────────────────────────

        private static XYZ GetStartPoint(MEPCurve curve)
            => (curve.Location as LocationCurve)?.Curve.GetEndPoint(0);

        private static XYZ GetEndPoint(MEPCurve curve)
            => (curve.Location as LocationCurve)?.Curve.GetEndPoint(1);

        /// <summary>
        /// Finds the unoccupied connector closest to the given point.
        /// If all connectors are occupied — falls back to the nearest one.
        /// </summary>
        private static Connector GetConnectorNear(MEPCurve curve, XYZ pt)
        {
            if (pt == null) return null;

            Connector best = null;
            double bestDist = double.MaxValue;

            // Priority — free connectors
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.IsConnected) continue;
                double d = c.Origin.DistanceTo(pt);
                if (d < bestDist) { bestDist = d; best = c; }
            }

            // Fallback — any nearest connector
            if (best == null)
            {
                bestDist = double.MaxValue;
                foreach (Connector c in curve.ConnectorManager.Connectors)
                {
                    double d = c.Origin.DistanceTo(pt);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
            }

            return best;
        }

        /// <summary>
        /// Two segments are collinear if their direction vectors are parallel.
        /// In that case no elbow is needed — it would just be a straight pipe.
        /// </summary>
        private static bool AreCollinear(MEPCurve a, MEPCurve b)
        {
            XYZ a0 = GetStartPoint(a), a1 = GetEndPoint(a);
            XYZ b0 = GetStartPoint(b), b1 = GetEndPoint(b);

            if (a0 == null || a1 == null || b0 == null || b1 == null) return false;

            double lenA = a0.DistanceTo(a1);
            double lenB = b0.DistanceTo(b1);
            if (lenA < 1e-6 || lenB < 1e-6) return false;

            XYZ dirA = (a1 - a0) / lenA;
            XYZ dirB = (b1 - b0) / lenB;

            return Math.Abs(dirA.DotProduct(dirB)) > 0.9999;
        }

        // ─────────────────────────────────────────────────────────────
        // Retrieve types from the document
        // ─────────────────────────────────────────────────────────────

        public static PipeType GetFirstPipeType(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No pipe type found in the project.");
        }

        public static DuctType GetFirstDuctType(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No duct type found in the project.");
        }

        public static PipingSystemType GetPipingSystemType(Document doc, string name = null)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>();

            if (name != null)
                return types.FirstOrDefault(t =>
                    t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? types.First();

            return types.First();
        }

        public static MechanicalSystemType GetMechSystemType(Document doc, string name = null)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>();

            if (name != null)
                return types.FirstOrDefault(t =>
                    t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? types.First();

            return types.First();
        }

        // ─────────────────────────────────────────────────────────────
        // Wall penetration warning
        // ─────────────────────────────────────────────────────────────

        public static void ShowPierceWarning(Document doc, List<XYZ> piercePts)
        {
            if (piercePts == null || piercePts.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"The route crosses {piercePts.Count} thin wall(s).");
            sb.AppendLine("Penetration point coordinates for sleeve placement (mm):");
            sb.AppendLine();

            foreach (var pt in piercePts)
                sb.AppendLine(
                    $"  X={pt.X * 304.8:F0}  " +
                    $"Y={pt.Y * 304.8:F0}  " +
                    $"Z={pt.Z * 304.8:F0}");

            Autodesk.Revit.UI.TaskDialog.Show(
                "BIM-Pathfinder A* — Wall Penetrations", sb.ToString());
        }
    }
}