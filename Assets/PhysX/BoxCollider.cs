using MagicPhysX;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public unsafe class BoxCollider : Collider
    {
        public Vector3 halfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        private Vector3 _lastHalfExtents;
        private Vector3 _lastScale;

        protected override void Awake()
        {
            base.Awake();
            CreateActor();
            RebuildShape();
        }

        protected override void Update()
        {
            base.Update();
            var scale = GetPhysicsScale();
            if (halfExtents != _lastHalfExtents || scale != _lastScale)
            {
                RebuildShape();
            }
        }

        private void RebuildShape()
        {
            if (_shape != null)
            {
                DetachShape(_shape);
                PxShape_release_mut(_shape);
                _shape = null;
            }

            var scale = GetPhysicsScale();
            var scaledHalfExtents = Vector3.Scale(halfExtents, scale);
            var geometry = PxBoxGeometry_new_1(scaledHalfExtents);
            if (PxBoxGeometry_isValid(&geometry))
            {
                CreateShape((PxGeometry*)&geometry);
                AttachShape(_shape);
            }

            _lastHalfExtents = halfExtents;
            _lastScale = scale;
        }
    }
}
