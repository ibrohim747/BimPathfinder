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
        // Pipe
        // ─────────────────────────────────────────────────────────────

        public List<Pipe> BuildPipes(
            List<XYZ> pathPts,
            PipeType pipeType,
            PipingSystemType systemType,
            double diameterMm)
        {
            if (pathPts == null || pathPts.Count < 2)
                throw new ArgumentException("Путь содержит менее 2 точек.");

            double diamFt = diameterMm / 304.8;
            var pipes = new List<Pipe>();

            for (int i = 0; i < pathPts.Count - 1; i++)
            {
                XYZ start = pathPts[i];
                XYZ end = pathPts[i + 1];

                if (start.DistanceTo(end) < 1e-6) continue;

                var pipe = Pipe.Create(
                    _doc,
                    systemType.Id,
                    pipeType.Id,
                    _level.Id,
                    start,
                    end);

                pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    ?.Set(diamFt);

                pipes.Add(pipe);
            }

            ConnectPipesWithElbows(pipes);

            return pipes;
        }

        // ─────────────────────────────────────────────────────────────
        // Duct
        // ─────────────────────────────────────────────────────────────

        public List<Duct> BuildDucts(
            List<XYZ> pathPts,
            DuctType ductType,
            MechanicalSystemType systemType,
            double widthMm,
            double heightMm)
        {
            if (pathPts == null || pathPts.Count < 2)
                throw new ArgumentException("Путь содержит менее 2 точек.");

            double wFt = widthMm / 304.8;
            double hFt = heightMm / 304.8;
            var ducts = new List<Duct>();

            for (int i = 0; i < pathPts.Count - 1; i++)
            {
                XYZ start = pathPts[i];
                XYZ end = pathPts[i + 1];

                if (start.DistanceTo(end) < 1e-6) continue;

                var duct = Duct.Create(
                    _doc,
                    systemType.Id,
                    ductType.Id,
                    _level.Id,
                    start,
                    end);

                duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(wFt);
                duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(hFt);

                ducts.Add(duct);
            }

            return ducts;
        }

        // ─────────────────────────────────────────────────────────────
        // Соединение труб фитингами (Elbow)
        // ─────────────────────────────────────────────────────────────

        private static void ConnectPipesWithElbows(List<Pipe> pipes)
        {
            for (int i = 0; i < pipes.Count - 1; i++)
            {
                var p1 = pipes[i];
                var p2 = pipes[i + 1];

                var c1 = GetConnectorClosestTo(p1, GetEndPoint(p1));
                var c2 = GetConnectorClosestTo(p2, GetStartPoint(p2));

                if (c1 == null || c2 == null) continue;

                try
                {
                    c1.ConnectTo(c2);
                }
                catch
                {

                }
            }
        }


        private static XYZ GetStartPoint(MEPCurve curve)
        {
            var loc = curve.Location as LocationCurve;
            return loc?.Curve.GetEndPoint(0);
        }

        private static XYZ GetEndPoint(MEPCurve curve)
        {
            var loc = curve.Location as LocationCurve;
            return loc?.Curve.GetEndPoint(1);
        }

        private static Connector GetConnectorClosestTo(MEPCurve curve, XYZ pt)
        {
            if (pt == null) return null;

            Connector closest = null;
            double minDist = double.MaxValue;

            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < minDist)
                {
                    minDist = d;
                    closest = c;
                }
            }

            return closest;
        }


        public static PipeType GetFirstPipeType(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "В проекте не найден ни один тип трубы.");
        }

        public static DuctType GetFirstDuctType(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "В проекте не найден ни один тип воздуховода.");
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

        public static void ShowPierceWarning(Document doc, List<XYZ> piercePts)
        {
            if (piercePts == null || piercePts.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Трасса пересекает {piercePts.Count} тонких стен.");
            sb.AppendLine("Координаты точек для размещения гильз (мм):");
            sb.AppendLine();

            foreach (var pt in piercePts)
            {
                sb.AppendLine($"  X={pt.X * 304.8:F0}  Y={pt.Y * 304.8:F0}  Z={pt.Z * 304.8:F0}");
            }

            Autodesk.Revit.UI.TaskDialog.Show(
                "BIM-Pathfinder A* — Пробивки стен", sb.ToString());
        }
    }
}