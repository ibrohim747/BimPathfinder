using System;
using Autodesk.Revit.DB;

namespace BimPathfinder
{
    internal enum NodeType
    {
        Free,
        HardObstacle,
        SoftObstacle
    }

    internal sealed class AStarNode : IComparable<AStarNode>
    {
        // ── Grid position ────────────────────────────────────────────
        public readonly int IX, IY, IZ;
        public readonly XYZ WorldPos;

        // ── Type and physical properties ─────────────────────────────
        public NodeType Type;
        public double WallThicknessFt;
        public bool NearWall;

        // ── A* costs ─────────────────────────────────────────────────
        public double G = double.MaxValue;
        public double H;
        public double F => G + H;

        // ── Penalties ────────────────────────────────────────────────
        public double PTurn;
        public double PSupport;
        public double PPierce;

        // ── Navigation ───────────────────────────────────────────────
        public AStarNode Parent;
        public XYZ IncomingDirection;
        public bool InOpenSet;
        public bool InClosedSet;

        public AStarNode(int ix, int iy, int iz, XYZ worldPos)
        {
            IX = ix; IY = iy; IZ = iz;
            WorldPos = worldPos;
        }

        public int CompareTo(AStarNode other)
        {
            int cmp = (G + H + PTurn + PSupport + PPierce)
                     .CompareTo(other.G + other.H + other.PTurn + other.PSupport + other.PPierce);
            return cmp != 0 ? cmp : H.CompareTo(other.H);
        }

        public void Reset()
        {
            G = double.MaxValue;
            H = 0;
            PTurn = PSupport = PPierce = 0;
            Parent = null;
            IncomingDirection = null;
            InOpenSet = InClosedSet = false;
        }
    }
}