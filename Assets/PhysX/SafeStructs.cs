using MagicPhysX;
using UnityEngine;

namespace PhysX
{
    public class ControllerShapeHit
    {
        public Collider collider;
        public Vector3 dir;
        public float length;
        public int triangleIndex;
        public Vector3 worldNormal;
        public Vector3 worldPos;
    }

    public struct OverlapHit
    {
        public Collider collider;
        public int faceIndex;
    }

    public struct RaycastHit
    {
        public Collider collider;
        public float distance;
        public int faceIndex;
        public PxHitFlags flags;
        public Vector3 normal;
        public Vector3 position;
        public float u;
        public float v;
    }

    public struct SweepHit
    {
        public Collider collider;
        public float distance;
        public int faceIndex;
        public PxHitFlags flags;
        public Vector3 normal;
        public Vector3 position;

        public Vector2 uv1 => QueryExtensions.GetUV(this, 0);
        public Vector2 uv2 => QueryExtensions.GetUV(this, 1);
    }
}
