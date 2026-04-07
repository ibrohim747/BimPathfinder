using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace BimPathfinder
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public sealed class BimPathfinderCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;

            try
            {
                // ── 1. Диалог настроек ───────────────────────────────
                var dialog = new PathfinderDialog();
                dialog.ShowDialog();

                if (!dialog.Confirmed)
                    return Result.Cancelled;

                double maxPierceMm = dialog.MaxPierceThicknessMm;
                double diamMm = dialog.DiameterMm;
                double stepMm = dialog.GridStepMm;
                var settings = dialog.Settings;
                var sysType = dialog.SystemType;

                // ── 2. Выбор точек пользователем ────────────────────
                TaskDialog.Show("BIM-Pathfinder A*",
                    "Кликните точку СТАРТА (коннектор или точка в модели).");

                XYZ startPt;
                try { startPt = uiDoc.Selection.PickPoint("Укажите точку СТАРТА"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                { return Result.Cancelled; }

                TaskDialog.Show("BIM-Pathfinder A*",
                    "Теперь кликните точку ФИНИША.");

                XYZ endPt;
                try { endPt = uiDoc.Selection.PickPoint("Укажите точку ФИНИША"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                { return Result.Cancelled; }

                // ── 3. Определение уровня и View3D ──────────────────
                var level = GetActiveLevel(doc, startPt);
                var view3D = GetOrCreate3DView(doc, uiApp.ActiveUIDocument);

                double zMin = level.ProjectElevation;
                double zMax = GetNextLevelElevation(doc, level);

                var adjustedStart = new XYZ(startPt.X, startPt.Y,
                    Math.Max(startPt.Z, zMin));
                var adjustedEnd = new XYZ(endPt.X, endPt.Y,
                    Math.Max(endPt.Z, zMin));

                // ── 4. Построение воксельной сетки ──────────────────
                using (var progress = new ProgressHelper("BIM-Pathfinder A*"))
                {
                    progress.Report("Построение воксельной сетки...");

                    var grid = new VoxelGrid(
                        doc, view3D,
                        new XYZ(adjustedStart.X, adjustedStart.Y, zMin),
                        new XYZ(adjustedEnd.X, adjustedEnd.Y, zMax),
                        maxPierceMm, stepMm);

                    progress.Report("Анализ препятствий...");
                    grid.AnalyzeObstacles();

                    // ── 5. Поиск пути A* ─────────────────────────────
                    progress.Report("Поиск пути A*...");

                    var solver = new AStarSolver(grid, settings);
                    var rawPath = solver.FindPath(adjustedStart, adjustedEnd);

                    // ── 6. Упрощение пути ────────────────────────────
                    progress.Report("Упрощение пути...");
                    var simplePath = PathSimplifier.FullSimplify(rawPath, grid.StepFt);

                    if (simplePath.Count < 2)
                        throw new InvalidOperationException(
                            "Путь слишком короткий после упрощения.");

                    // ── 7. Генерация элементов в Revit ───────────────
                    progress.Report("Генерация элементов MEP...");

                    using (var tx = new Transaction(doc, "BIM-Pathfinder: создание трассы"))
                    {
                        tx.Start();

                        var builder = new RevitBuilder(doc, level);

                        if (sysType == MepSystemType.Pipe)
                        {
                            var pipeType = RevitBuilder.GetFirstPipeType(doc);
                            var pipeSysType = RevitBuilder.GetPipingSystemType(doc);
                            builder.BuildPipes(simplePath, pipeType, pipeSysType, diamMm);
                        }
                        else
                        {
                            var ductType = RevitBuilder.GetFirstDuctType(doc);
                            var mechSysType = RevitBuilder.GetMechSystemType(doc);
                            builder.BuildDucts(simplePath, ductType, mechSysType,
                                               diamMm, diamMm * 0.6); // высота = 60% ширины
                        }

                        tx.Commit();
                    }

                    // ── 8. Предупреждение о пробивках ────────────────
                    var piercePoints = solver.GetPiercedWallPoints(simplePath);
                    if (piercePoints.Count > 0)
                        RevitBuilder.ShowPierceWarning(doc, piercePoints);

                    // ── 9. Итоговое сообщение ─────────────────────────
                    TaskDialog.Show("BIM-Pathfinder A* — Готово",
                        $"Трасса построена успешно.\n" +
                        $"Сегментов: {simplePath.Count - 1}\n" +
                        $"Точек поворота: {simplePath.Count - 2}\n" +
                        $"Пробивок стен: {piercePoints.Count}");
                }

                return Result.Succeeded;
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
                TaskDialog.Show("BIM-Pathfinder A* — Ошибка", ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = $"Непредвиденная ошибка: {ex.Message}";
                TaskDialog.Show("BIM-Pathfinder A* — Ошибка", message);
                return Result.Failed;
            }
        }

        private static Level GetActiveLevel(Document doc, XYZ pt)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .ToList();

            return levels.LastOrDefault(l => l.ProjectElevation <= pt.Z + 1e-3)
                ?? levels.FirstOrDefault()
                ?? throw new InvalidOperationException("В проекте нет уровней.");
        }

        private static double GetNextLevelElevation(Document doc, Level current)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .ToList();

            var next = levels.FirstOrDefault(l =>
                l.ProjectElevation > current.ProjectElevation + 1e-3);

            return next?.ProjectElevation
                ?? (current.ProjectElevation + 3000 / 304.8);
        }

        private static View3D GetOrCreate3DView(Document doc, UIDocument uiDoc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective);

            if (existing != null) return existing;

            View3D view3D;
            using (var tx = new Transaction(doc, "BIM-Pathfinder: создание 3D вида"))
            {
                tx.Start();
                var viewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .First(t => t.ViewFamily == ViewFamily.ThreeDimensional);

                view3D = View3D.CreateIsometric(doc, viewFamilyType.Id);
                view3D.Name = "BIM-Pathfinder_Temp";
                tx.Commit();
            }

            return view3D;
        }
    }

    internal sealed class ProgressHelper : IDisposable
    {
        private readonly string _title;

        public ProgressHelper(string title) { _title = title; }

        public void Report(string message)
        {

            System.Diagnostics.Debug.WriteLine($"[{_title}] {message}");
        }

        public void Dispose() { }
    }
}