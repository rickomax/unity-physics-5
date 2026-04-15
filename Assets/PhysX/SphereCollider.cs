using MagicPhysX;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public unsafe class SphereCollider : Collider
    {
        [SerializeField] private float _radius = 0.5f;

        public float radius
        {
            get => _radius;
            set
            {
                if (_radius == value)
                {
                    return;
                }

                _radius = value;
                _shapeDirty = true;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            CreateActor();
            RebuildShape();
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
        }
    }
}
