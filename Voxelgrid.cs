using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Visual;

namespace BimPathfinder
{
    internal sealed class VoxelGrid
    {
        // ── Константы (переводим мм → футы) ─────────────────────────
        private const double MmToFt = 1.0 / 304.8;

        public const double DefaultStepFt = 200 * MmToFt; 
        public const double OffsetFt = 1000 * MmToFt; 
        public const double SupportDistFt = 300 * MmToFt; 

        // ── Параметры сетки ──────────────────────────────────────────
        public readonly double StepFt;
        public readonly XYZ Origin;
        public readonly int NX, NY, NZ;

        // ── Массив узлов ─────────────────────────────────────────────
        private readonly AStarNode[,,] _nodes;

        // ── Ссылки на Revit ──────────────────────────────────────────
        private readonly Document _doc;
        private readonly View3D _view3D;
        private readonly double _maxPierceFt;

        // ── Публичные свойства ───────────────────────────────────────
        public BoundingBoxXYZ Bounds { get; private set; }

        // ─────────────────────────────────────────────────────────────
        public VoxelGrid(Document doc, View3D view3D,
                         XYZ startPt, XYZ endPt,
                         double maxPierceThicknessMm,
                         double stepMm = 200.0)
        {
            _doc = doc;
            _view3D = view3D;
            _maxPierceFt = maxPierceThicknessMm * MmToFt;
            StepFt = stepMm * MmToFt;

            // Bounding box с отступом
            double minX = Math.Min(startPt.X, endPt.X) - OffsetFt;
            double minY = Math.Min(startPt.Y, endPt.Y) - OffsetFt;
            double minZ = Math.Min(startPt.Z, endPt.Z);

            double maxX = Math.Max(startPt.X, endPt.X) + OffsetFt;
            double maxY = Math.Max(startPt.Y, endPt.Y) + OffsetFt;
            double maxZ = Math.Max(startPt.Z, endPt.Z);

            // Вертикальные границы — текущий этаж (Z задаётся снаружи через уровни)
            Bounds = new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };

            Origin = Bounds.Min;

            NX = Math.Max(1, (int)Math.Ceiling((maxX - minX) / StepFt));
            NY = Math.Max(1, (int)Math.Ceiling((maxY - minY) / StepFt));
            NZ = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / StepFt));

            _nodes = new AStarNode[NX, NY, NZ];
            InitNodes();
        }

        // ── Инициализация координат узлов ────────────────────────────
        private void InitNodes()
        {
            for (int ix = 0; ix < NX; ix++)
                for (int iy = 0; iy < NY; iy++)
                    for (int iz = 0; iz < NZ; iz++)
                    {
                        var pos = new XYZ(
                            Origin.X + ix * StepFt,
                            Origin.Y + iy * StepFt,
                            Origin.Z + iz * StepFt);
                        _nodes[ix, iy, iz] = new AStarNode(ix, iy, iz, pos);
                    }
        }

        public void AnalyzeObstacles()
        {
            var hardCats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Ceilings,
            };

            var wallFilter = new ElementCategoryFilter(BuiltInCategory.OST_Walls);
            var hardFilter = new ElementMulticategoryFilter(hardCats);
            var unionFilter = new LogicalOrFilter(wallFilter, hardFilter);

            var intersector = new ReferenceIntersector(
                unionFilter,
                FindReferenceTarget.Element,
                _view3D)
            {
                FindReferencesInRevitLinks = false
            };

            for (int ix = 0; ix < NX; ix++)
                for (int iy = 0; iy < NY; iy++)
                    for (int iz = 0; iz < NZ; iz++)
                    {
                        var node = _nodes[ix, iy, iz];
                        ClassifyNode(node, intersector);
                    }

            MarkSupportNodes();
        }

        // ── Классификация одного узла ────────────────────────────────
        private void ClassifyNode(AStarNode node, ReferenceIntersector intersector)
        {
            var pt = node.WorldPos;
            var dir = XYZ.BasisX;

            var hits = intersector.Find(pt, dir);
            if (hits == null || hits.Count == 0)
            {
                hits = intersector.Find(pt, XYZ.BasisX.Negate());
                if (hits == null || hits.Count == 0) return;
            }

            foreach (var hit in hits)
            {
                if (hit.Proximity > StepFt * 0.5) continue;

                var elem = _doc.GetElement(hit.GetReference().ElementId);
                if (elem == null) continue;

                if (elem is Wall wall)
                {
                    double thickFt = wall.WallType.Width;
                    if (_maxPierceFt > 0 && thickFt <= _maxPierceFt)
                    {
                        node.Type = NodeType.SoftObstacle;
                        node.WallThicknessFt = thickFt;
                    }
                    else
                    {
                        node.Type = NodeType.HardObstacle;
                    }
                    return;
                }

                node.Type = NodeType.HardObstacle;
                return;
            }
        }

        // ── Анализ близости к поверхностям (для штрафа PSupport) ────
        private void MarkSupportNodes()
        {
            int[] dx = { 1, -1, 0, 0, 0, 0 };
            int[] dy = { 0, 0, 1, -1, 0, 0 };
            int[] dz = { 0, 0, 0, 0, 1, -1 };

            int supportSteps = (int)Math.Ceiling(SupportDistFt / StepFt);

            for (int ix = 0; ix < NX; ix++)
                for (int iy = 0; iy < NY; iy++)
                    for (int iz = 0; iz < NZ; iz++)
                    {
                        var node = _nodes[ix, iy, iz];
                        if (node.Type == NodeType.HardObstacle) continue;

                        bool near = false;
                        for (int d = 0; d < 6 && !near; d++)
                        {
                            for (int s = 1; s <= supportSteps; s++)
                            {
                                int nx2 = ix + dx[d] * s;
                                int ny2 = iy + dy[d] * s;
                                int nz2 = iz + dz[d] * s;

                                if (!InBounds(nx2, ny2, nz2)) { near = true; break; }
                                if (_nodes[nx2, ny2, nz2].Type == NodeType.HardObstacle)
                                {
                                    near = true; break;
                                }
                            }
                        }
                        node.NearWall = near;
                    }
        }

        // ── Публичные методы доступа ─────────────────────────────────
        public AStarNode GetNode(int ix, int iy, int iz)
        {
            if (!InBounds(ix, iy, iz)) return null;
            return _nodes[ix, iy, iz];
        }

        public AStarNode GetNodeAt(XYZ worldPt)
        {
            int ix = (int)Math.Round((worldPt.X - Origin.X) / StepFt);
            int iy = (int)Math.Round((worldPt.Y - Origin.Y) / StepFt);
            int iz = (int)Math.Round((worldPt.Z - Origin.Z) / StepFt);
            return GetNode(ix, iy, iz);
        }

        public bool InBounds(int ix, int iy, int iz)
            => ix >= 0 && ix < NX && iy >= 0 && iy < NY && iz >= 0 && iz < NZ;

        public void ResetPathData()
        {
            for (int ix = 0; ix < NX; ix++)
                for (int iy = 0; iy < NY; iy++)
                    for (int iz = 0; iz < NZ; iz++)
                        _nodes[ix, iy, iz].Reset();
        }

        public List<(AStarNode node, XYZ dir)> GetNeighbors(AStarNode n)
        {
            var result = new List<(AStarNode, XYZ)>(6);

            (int dx, int dy, int dz, XYZ dir)[] moves =
            {
                ( 1,  0,  0, XYZ.BasisX),
                (-1,  0,  0, XYZ.BasisX.Negate()),
                ( 0,  1,  0, XYZ.BasisY),
                ( 0, -1,  0, XYZ.BasisY.Negate()),
                ( 0,  0,  1, XYZ.BasisZ),
                ( 0,  0, -1, XYZ.BasisZ.Negate()),
            };

            foreach (var m in moves)
            {
                var nb = GetNode(n.IX + m.dx, n.IY + m.dy, n.IZ + m.dz);
                if (nb != null && nb.Type != NodeType.HardObstacle)
                    result.Add((nb, m.dir));
            }

            return result;
        }
    }
}