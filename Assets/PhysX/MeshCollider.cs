using MagicPhysX;
using System;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public unsafe class MeshCollider : Collider
    {
        [SerializeField] private bool _convex;
        [SerializeField] private Mesh _sharedMesh;

        public bool convex
        {
            get => _convex;
            set
            {
                if (_convex == value)
                {
                    return;
                }
                _convex = value;
                RebuildShape();
            }
        }

        public Mesh sharedMesh
        {
            get => _sharedMesh;
            set
            {
                if (_sharedMesh == value)
                {
                    return;
                }
                _sharedMesh = value;
                RebuildShape();
            }
        }

        public PxTriangleMesh* triangleMesh { get; private set; }
        public PxConvexMesh* convexMesh { get; private set; }

        protected override void Awake()
        {
            base.Awake();
            if (_sharedMesh != null)
            {
                RebuildShape();
            }
        }

        public override void Release()
        {
            if (_released)
            {
                return;
            }
            triangleMesh = null;
            convexMesh = null;
            base.Release();
        }

        public override void RebuildShape()
        {
            PxShapeFlags flags = 0;
            if (shape != null)
            {
                flags = PxShape_getFlags(shape);
            }
            DestroyShape();
            triangleMesh = null;
            convexMesh = null;
            if (_sharedMesh == null)
            {
                return;
            }
            if (!_sharedMesh.isReadable)
            {
                Debug.LogWarning($"MeshCollider on '{name}': mesh is not readable.");
                return;
            }
            var scale = GetPhysicsScale();
            if (!_convex)
            {
                if (!isPhysicsStatic && !isKinematic)
                {
                    Debug.LogWarning("Triangular concave meshes can't be assigned to dynamic rigidbodies.");
                    return;
                }
                PhysicsManager.instance.BakeMesh(_sharedMesh, convex: false);
                triangleMesh = PhysicsManager.instance.GetTriangleMesh(_sharedMesh);
                if (triangleMesh == null)
                {
                    Debug.LogError("Could not retrieve baked PhysX triangle mesh.");
                    return;
                }
                var meshScale = new PxMeshScale { rotation = Quaternion.identity, scale = scale };
                var geometry = PxTriangleMeshGeometry_new(triangleMesh, &meshScale, 0);
                if (!PxTriangleMeshGeometry_isValid(&geometry))
                {
                    Debug.LogError("Could not generate PhysX triangle mesh geometry.");
                    return;
                }
                CreateShape((PxGeometry*)&geometry);
            }
            else
            {
                PhysicsManager.instance.BakeMesh(_sharedMesh, convex: true);
                convexMesh = PhysicsManager.instance.GetConvexMesh(_sharedMesh);
                if (convexMesh == null)
                {
                    Debug.LogError("Could not retrieve baked PhysX convex mesh.");
                    return;
                }
                var meshScale = new PxMeshScale { rotation = Quaternion.identity, scale = scale };
                var geometry = PxConvexMeshGeometry_new(convexMesh, &meshScale, 0);
                if (!PxConvexMeshGeometry_isValid(&geometry))
                {
                    Debug.LogError("Could not generate PhysX convex mesh geometry.");
                    return;
                }
                CreateShape((PxGeometry*)&geometry);
            }
            if (shape != null)
            {
                PxShape_setFlags_mut(shape, flags);
                AttachShape(shape);
            }
        }
    }
}
