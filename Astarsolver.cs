using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;

namespace BimPathfinder
{
    public sealed class PathfinderSettings
    {
        public double PenaltyTurn { get; set; } = 2.0;

        public double PenaltySupport { get; set; } = 0.5;

        public double PenaltyPierce { get; set; } = 10.0;

        public double CeilingBonus { get; set; } = -0.2;

        public double WallBonus { get; set; } = -0.1;
    }

    internal sealed class AStarSolver
    {
        private readonly VoxelGrid _grid;
        private readonly PathfinderSettings _settings;

        private readonly SortedSet<AStarNode> _openSet;
        private int _tieBreaker;

        public AStarSolver(VoxelGrid grid, PathfinderSettings settings)
        {
            _grid = grid;
            _settings = settings;
            _openSet = new SortedSet<AStarNode>(Comparer<AStarNode>.Create(CompareNodes));
        }

        // ── SortedSet comparator ─────────────────────────────────────
        private static int CompareNodes(AStarNode a, AStarNode b)
        {
            double fa = TotalCost(a);
            double fb = TotalCost(b);
            if (Math.Abs(fa - fb) > 1e-9)
                return fa.CompareTo(fb);
            if (Math.Abs(a.H - b.H) > 1e-9)
                return a.H.CompareTo(b.H);
            return a.GetHashCode().CompareTo(b.GetHashCode());
        }

        private static double TotalCost(AStarNode n)
            => n.G + n.H + n.PTurn + n.PSupport + n.PPierce;

        // ── Heuristic (Manhattan distance) ───────────────────────────
        private double Heuristic(AStarNode a, AStarNode goal)
        {
            return (Math.Abs(a.IX - goal.IX)
                  + Math.Abs(a.IY - goal.IY)
                  + Math.Abs(a.IZ - goal.IZ)) * _grid.StepFt;
        }

        public List<XYZ> FindPath(XYZ startPt, XYZ endPt, XYZ startDir = null)
        {
            _grid.ResetPathData();
            _openSet.Clear();

            var startNode = _grid.GetNodeAt(startPt);
            var goalNode = _grid.GetNodeAt(endPt);

            if (startNode == null || goalNode == null)
                throw new InvalidOperationException(
                    "Start or end point is outside the grid.");

            if (goalNode.Type == NodeType.HardObstacle)
                throw new InvalidOperationException(
                    "The end point is located inside an impassable obstacle.");

            startNode.G = 0;
            startNode.H = Heuristic(startNode, goalNode);
            startNode.IncomingDirection = startDir;

            _openSet.Add(startNode);
            startNode.InOpenSet = true;

            int iterations = 0;
            const int MaxIterations = 500_000;

            while (_openSet.Count > 0)
            {
                if (++iterations > MaxIterations)
                    throw new InvalidOperationException(
                        $"A* exceeded the iteration limit ({MaxIterations}). " +
                        "Try increasing the grid step or expanding MaxPiercingThickness.");

                var current = _openSet.Min;
                _openSet.Remove(current);
                current.InOpenSet = false;
                current.InClosedSet = true;

                if (current.IX == goalNode.IX &&
                    current.IY == goalNode.IY &&
                    current.IZ == goalNode.IZ)
                {
                    return ReconstructPath(current, startPt, endPt);
                }

                foreach (var (neighbor, moveDir) in _grid.GetNeighbors(current))
                {
                    if (neighbor.InClosedSet) continue;

                    if (current == startNode && startDir != null)
                    {
                        if (!IsParallel(moveDir, startDir)) continue;
                    }

                    double stepCost = _grid.StepFt;

                    double turnPenalty = 0;
                    if (current.IncomingDirection != null &&
                        !IsParallel(moveDir, current.IncomingDirection))
                    {
                        turnPenalty = _settings.PenaltyTurn;
                    }

                    double supportPenalty = neighbor.NearWall ? 0 : _settings.PenaltySupport;

                    double surfaceBonus = 0;
                    if (neighbor.NearWall)
                    {
                        surfaceBonus = IsNearCeiling(neighbor)
                            ? _settings.CeilingBonus
                            : _settings.WallBonus;
                    }

                    double piercePenalty = 0;
                    if (neighbor.Type == NodeType.SoftObstacle)
                        piercePenalty = neighbor.WallThicknessFt * _settings.PenaltyPierce;

                    double tentativeG = current.G + stepCost + surfaceBonus;

                    if (tentativeG < neighbor.G)
                    {
                        if (neighbor.InOpenSet)
                            _openSet.Remove(neighbor);

                        neighbor.G = tentativeG;
                        neighbor.H = Heuristic(neighbor, goalNode);
                        neighbor.PTurn = turnPenalty;
                        neighbor.PSupport = supportPenalty;
                        neighbor.PPierce = piercePenalty;
                        neighbor.Parent = current;
                        neighbor.IncomingDirection = moveDir;

                        _openSet.Add(neighbor);
                        neighbor.InOpenSet = true;
                    }
                }
            }

            throw new InvalidOperationException(
                "Path not found. Check that a passage exists between the points.");
        }

        // ── Path reconstruction ──────────────────────────────────────
        private List<XYZ> ReconstructPath(AStarNode goalNode, XYZ realStart, XYZ realEnd)
        {
            var pts = new List<XYZ>();
            var cur = goalNode;

            while (cur != null)
            {
                pts.Add(cur.WorldPos);
                cur = cur.Parent;
            }

            pts.Reverse();

            if (pts.Count > 0) pts[0] = realStart;
            if (pts.Count > 1) pts[pts.Count - 1] = realEnd;

            return pts;
        }

        // ── Helper methods ───────────────────────────────────────────
        private static bool IsParallel(XYZ a, XYZ b)
        {
            double dot = Math.Abs(a.DotProduct(b));
            return dot > 0.99;
        }

        private bool IsNearCeiling(AStarNode node)
        {
            var above = _grid.GetNode(node.IX, node.IY, node.IZ + 1);
            return above != null && above.Type == NodeType.HardObstacle;
        }

        public List<XYZ> GetPiercedWallPoints(List<XYZ> path)
        {
            var result = new List<XYZ>();
            foreach (var pt in path)
            {
                var node = _grid.GetNodeAt(pt);
                if (node != null && node.Type == NodeType.SoftObstacle)
                    result.Add(pt);
            }
            return result;
        }
    }
}