using MagicPhysX;
using System;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public unsafe class MeshCollider : Collider
    {
        public bool convex;

        [SerializeField]
        private Mesh _sharedMesh;
        private Mesh _lastSharedMesh;
        private Vector3 _lastScale;
        private bool _lastPhysicsStatic;
        private bool _lastKinematic;

        public PxTriangleMesh* triangleMesh { get; private set; }
        public PxConvexMesh* convexMesh { get; private set; }

        public Mesh sharedMesh
        {
            get => _sharedMesh;
            set => _sharedMesh = value;
        }

        protected override void Awake()
        {
            base.Awake();
            CreateActor();
            _lastScale = GetPhysicsScale();
            _lastPhysicsStatic = actorIsStatic;
            _lastKinematic = actorIsKinematic;
            if (_sharedMesh != null)
            {
                RebuildShape();
            }
        }

        protected override void Update()
        {
            base.Update();

            var needsRebuild = false;

            if (actorIsStatic != _lastPhysicsStatic)
            {
                _lastPhysicsStatic = actorIsStatic;
                _lastKinematic = actorIsKinematic;
                needsRebuild = true;
            }

            if (_sharedMesh != _lastSharedMesh)
            {
                needsRebuild = true;
            }

            var currentKinematic = actorIsKinematic;
            if (!convex && currentKinematic != _lastKinematic)
            {
                _lastKinematic = currentKinematic;
                needsRebuild = true;
            }

            var scale = GetPhysicsScale();
            if (_sharedMesh != null && scale != _lastScale)
            {
                needsRebuild = true;
            }

            if (needsRebuild)
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
            var scale = GetPhysicsScale();
            if (_sharedMesh == null || !_sharedMesh.isReadable)
            {
                ReleaseShape();
                _lastSharedMesh = _sharedMesh;
                _lastScale = scale;
                _lastKinematic = actorIsKinematic;
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

            if (!convex)
            {
                if (!TriangleMeshAllowedForCurrentActor())
                {
                    Debug.LogWarning("Triangular concave meshes can't be assigned to dynamic rigidbodies");
                    _lastSharedMesh = _sharedMesh;
                    _lastScale = scale;
                    return;
                }

                PhysicsManager.instance.BakeMesh(_sharedMesh, convex: false);
                triangleMesh = PhysicsManager.instance.GetTriangleMesh(_sharedMesh);
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
                PhysicsManager.instance.BakeMesh(_sharedMesh, convex: true);
                convexMesh = PhysicsManager.instance.GetConvexMesh(_sharedMesh);
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

            _lastSharedMesh = _sharedMesh;
            _lastScale = scale;
            _lastKinematic = actorIsKinematic;
        }
    }
}
