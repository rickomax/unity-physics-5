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
                RebuildShape();
            }
        }

        protected override void Awake()
        {
            base.Awake();
            RebuildShape();
        }

        public override void RebuildShape()
        {
            DestroyShape();
            var scale = GetPhysicsScale();
            var scaledRadius = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) * _radius;
            var geometry = PxSphereGeometry_new(scaledRadius);
            if (PxSphereGeometry_isValid(&geometry))
            {
                CreateShape((PxGeometry*)&geometry);
                AttachShape(_shape);
            }
        }
    }
}
