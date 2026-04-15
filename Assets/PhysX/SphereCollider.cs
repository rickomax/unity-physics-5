using MagicPhysX;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public unsafe class SphereCollider : Collider
    {
        public float radius = 0.5f;

        private float _lastRadius;
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
            if (radius != _lastRadius || scale != _lastScale)
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
            var scaledRadius = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) * radius;
            var geometry = PxSphereGeometry_new(scaledRadius);
            if (PxSphereGeometry_isValid(&geometry))
            {
                CreateShape((PxGeometry*)&geometry);
                AttachShape(_shape);
            }

            _lastRadius = radius;
            _lastScale = scale;
        }
    }
}
