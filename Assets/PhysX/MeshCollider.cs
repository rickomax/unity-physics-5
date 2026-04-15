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
                _shapeDirty = true;
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
                _shapeDirty = true;
            }
        }

        public PxTriangleMesh* triangleMesh { get; private set; }
        public PxConvexMesh* convexMesh { get; private set; }

        protected override void Awake()
        {
            base.Awake();
            CreateActor();
            if (sharedMesh != null)
            {
                RebuildShape();
            }
        }

        protected override void Update()
        {
            base.Update();
            if (_shapeDirty || _transform.hasChanged)
            {
                RebuildShape();
                _shapeDirty = false;
                _transform.hasChanged = false;
            }
        }

        public override void SetShapeDirty()
        {
            _shapeDirty = true;
        }

        public override void Release()
        {
            if (_released)
            {
                return;
            }

            ReleaseShape();
            base.Release();
        }

        private bool TriangleMeshAllowedForCurrentActor() => actorIsStatic || actorIsKinematic;

        private void ReleaseShape()
        {
            if (_shape != null)
            {
                DetachShape(_shape);
                PxShape_release_mut(_shape);
                _shape = null;
            }

            triangleMesh = null;
            convexMesh = null;
        }

        private void RebuildShape()
        {
            if (sharedMesh == null || !sharedMesh.isReadable)
            {
                ReleaseShape();
                return;
            }

            if (actor == null)
            {
                CreateActor();
                if (actor == null)
                {
                    return;
                }
            }

            ReleaseShape();

            var scale = GetPhysicsScale();

            if (!convex)
            {
                if (!TriangleMeshAllowedForCurrentActor())
                {
                    Debug.LogWarning("Triangular concave meshes can't be assigned to dynamic rigidbodies");
                    return;
                }

                PhysicsManager.instance.BakeMesh(sharedMesh, convex: false);
                triangleMesh = PhysicsManager.instance.GetTriangleMesh(sharedMesh);
                if (triangleMesh == null)
                {
                    throw new Exception("Could not retrieve baked PhysX triangle mesh");
                }

                var meshScale = new PxMeshScale { rotation = Quaternion.identity, scale = scale };
                var geometry = PxTriangleMeshGeometry_new(triangleMesh, &meshScale, 0);
                if (!PxTriangleMeshGeometry_isValid(&geometry))
                {
                    throw new Exception("Could not generate PhysX triangle mesh geometry");
                }

                CreateShape((PxGeometry*)&geometry);
            }
            else
            {
                PhysicsManager.instance.BakeMesh(sharedMesh, convex: true);
                convexMesh = PhysicsManager.instance.GetConvexMesh(sharedMesh);
                if (convexMesh == null)
                {
                    throw new Exception("Could not retrieve baked PhysX convex mesh");
                }

                var meshScale = new PxMeshScale { rotation = Quaternion.identity, scale = scale };
                var geometry = PxConvexMeshGeometry_new(convexMesh, &meshScale, 0);
                if (!PxConvexMeshGeometry_isValid(&geometry))
                {
                    throw new Exception("Could not generate PhysX convex mesh geometry");
                }

                CreateShape((PxGeometry*)&geometry);
            }

            if (_shape != null)
            {
                AttachShape(_shape);
            }
        }
    }
}
