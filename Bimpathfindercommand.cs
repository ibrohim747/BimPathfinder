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
                // ── 1. Settings dialog ───────────────────────────────
                var dialog = new PathfinderDialog(doc);
                dialog.ShowDialog();

                if (!dialog.Confirmed)
                    return Result.Cancelled;

                double maxPierceMm = dialog.MaxPierceThicknessMm;
                double diamMm = dialog.DiameterMm;
                double stepMm = dialog.GridStepMm;
                var settings = dialog.Settings;
                var sysType = dialog.SystemType;

                // ── 2. FIXED: obtain View3D BEFORE picking points ────
                // View3D is created in a separate transaction that is fully
                // committed before it is passed to ReferenceIntersector —
                // otherwise a NullReferenceException is thrown.
                View3D view3D = GetOrCreate3DView(doc);
                if (view3D == null)
                    throw new InvalidOperationException(
                        "Could not find or create a 3D view.\n" +
                        "Open any orthographic 3D view in Revit and try again.");

                // ── 3. Pick points ───────────────────────────────────
                TaskDialog.Show("BIM-Pathfinder A*",
                    "Click the START point (connector or a point in the model).");

                XYZ startPt;
                try { startPt = uiDoc.Selection.PickPoint("Pick the START point"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                { return Result.Cancelled; }

                TaskDialog.Show("BIM-Pathfinder A*", "Now click the END point.");

                XYZ endPt;
                try { endPt = uiDoc.Selection.PickPoint("Pick the END point"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                { return Result.Cancelled; }

                // ── 4. Level ─────────────────────────────────────────
                var level = GetActiveLevel(doc, startPt);
                double zMin = level.ProjectElevation;
                double zMax = GetNextLevelElevation(doc, level);

                var adjustedStart = new XYZ(startPt.X, startPt.Y,
                    Clamp(startPt.Z, zMin, zMax));
                var adjustedEnd = new XYZ(endPt.X, endPt.Y,
                    Clamp(endPt.Z, zMin, zMax));

                // ── 5. FIXED: VoxelGrid with null guard ───────────────
                VoxelGrid grid;
                AStarSolver solver;
                List<XYZ> rawPath;

                try
                {
                    grid = new VoxelGrid(
                        doc, view3D,
                        new XYZ(adjustedStart.X, adjustedStart.Y, zMin),
                        new XYZ(adjustedEnd.X, adjustedEnd.Y, zMax),
                        maxPierceMm, stepMm);

                    grid.AnalyzeObstacles();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Error building the voxel grid:\n{ex.Message}\n\n" +
                        "Make sure walls and floors are not hidden in the 3D view.", ex);
                }

                // ── 6. A* ────────────────────────────────────────────
                try
                {
                    solver = new AStarSolver(grid, settings);
                    rawPath = solver.FindPath(adjustedStart, adjustedEnd);
                }
                catch (InvalidOperationException ex)
                {
                    TaskDialog.Show("BIM-Pathfinder A* — Path Not Found", ex.Message);
                    return Result.Failed;
                }

                // ── 7. Simplification ────────────────────────────────
                var simplePath = PathSimplifier.FullSimplify(rawPath, grid.StepFt);

                if (simplePath == null || simplePath.Count < 2)
                    throw new InvalidOperationException(
                        "The path is too short after simplification.\n" +
                        "Choose points that are farther apart.");

                // ── 8. Create MEP elements ───────────────────────────
                using (var tx = new Transaction(doc, "BIM-Pathfinder: create route"))
                {
                    tx.Start();

                    var builder = new RevitBuilder(doc, level);

                    if (sysType == MepSystemType.Pipe)
                    {
                        var pipeType = dialog.SelectedPipeType
                                          ?? RevitBuilder.GetFirstPipeType(doc);
                        var pipeSysType = RevitBuilder.GetPipingSystemType(doc);
                        builder.BuildPipes(simplePath, pipeType, pipeSysType, diamMm);
                    }
                    else
                    {
                        var ductType = dialog.SelectedDuctType
                                          ?? RevitBuilder.GetFirstDuctType(doc);
                        var mechSysType = RevitBuilder.GetMechSystemType(doc);
                        builder.BuildDucts(simplePath, ductType, mechSysType,
                                           diamMm, diamMm * 0.6);
                    }

                    tx.Commit();
                }

                // ── 9. Wall penetrations ─────────────────────────────
                var solver2 = new AStarSolver(grid, settings);
                var piercePoints = solver2.GetPiercedWallPoints(simplePath);
                if (piercePoints.Count > 0)
                    RevitBuilder.ShowPierceWarning(doc, piercePoints);

                TaskDialog.Show("BIM-Pathfinder A* — Done",
                    $"Route created successfully.\n" +
                    $"Segments: {simplePath.Count - 1}\n" +
                    $"Turn points: {Math.Max(0, simplePath.Count - 2)}\n" +
                    $"Wall penetrations: {piercePoints.Count}");

                return Result.Succeeded;
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
                TaskDialog.Show("BIM-Pathfinder A* — Error", ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = $"Unexpected error: {ex.Message}";
                TaskDialog.Show("BIM-Pathfinder A* — Error",
                    $"{message}\n\nStack:\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // FIXED: GetOrCreate3DView
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns an existing orthographic 3D view.
        /// If none is found — creates one in a SEPARATE committed transaction.
        ///
        /// Key fixes:
        /// 1. Search for an existing view first — avoid creating duplicates.
        /// 2. If creating — the transaction is fully committed BEFORE
        ///    the object is returned. Otherwise ReferenceIntersector receives
        ///    an invalid element and throws NullReferenceException.
        /// 3. Far Clipping is disabled — required for ReferenceIntersector.
        /// </summary>
        private static View3D GetOrCreate3DView(Document doc)
        {
            const string pluginViewName = "BIM-Pathfinder_RayTrace";

            // Step 1: look for the plugin's dedicated view
            var special = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v =>
                    !v.IsTemplate && !v.IsPerspective && v.Name == pluginViewName);
            if (special != null) return special;

            // Step 2: any orthographic 3D view
            var anyOrtho = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective);
            if (anyOrtho != null) return anyOrtho;

            // Step 3: create a new one in its own transaction
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);

            if (vft == null) return null;

            View3D created = null;
            using (var tx = new Transaction(doc, "BIM-Pathfinder: 3D view"))
            {
                tx.Start();
                try
                {
                    created = View3D.CreateIsometric(doc, vft.Id);
                    created.Name = pluginViewName;

                    // Disable Far Clipping.
                    // Without this ReferenceIntersector cannot see distant elements.
                    var farClip = created.get_Parameter(
                        BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
                    farClip?.Set(0); // 0 = No clip

                    tx.Commit();
                }
                catch
                {
                    tx.RollBack();
                    return null;
                }
            }

            return created;
        }

        // ─────────────────────────────────────────────────────────────
        private static Level GetActiveLevel(Document doc, XYZ pt)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .ToList();

            if (levels.Count == 0)
                throw new InvalidOperationException("The project contains no levels.");

            return levels.LastOrDefault(l => l.ProjectElevation <= pt.Z + 1e-3)
                ?? levels.First();
        }

        private static double GetNextLevelElevation(Document doc, Level current)
        {
            var next = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .FirstOrDefault(l =>
                    l.ProjectElevation > current.ProjectElevation + 1e-3);

            return next?.ProjectElevation
                ?? (current.ProjectElevation + 3000.0 / 304.8);
        }

        private static double Clamp(double v, double min, double max)
            => v < min ? min : v > max ? max : v;
    }

    // ─────────────────────────────────────────────────────────────────
    internal sealed class ProgressHelper : IDisposable
    {
        private readonly string _title;
        public ProgressHelper(string title) { _title = title; }
        public void Report(string msg) =>
            System.Diagnostics.Debug.WriteLine($"[{_title}] {msg}");
        public void Dispose() { }
    }
}