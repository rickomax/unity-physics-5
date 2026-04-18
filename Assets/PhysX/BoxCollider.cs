using MagicPhysX;
using System;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public unsafe class BoxCollider : Collider
    {
        [SerializeField] private Vector3 _halfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        public Vector3 halfExtents
        {
            get => _halfExtents;
            set
            {
                if (_halfExtents == value)
                {
                    return;
                }
                _halfExtents = value;
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
            PxShapeFlags flags = 0;
            if (shape != null)
            {
                flags = PxShape_getFlags(shape);
            }
            DestroyShape();
            var scaledHalfExtents = Vector3.Scale(_halfExtents, GetPhysicsScale());
            var geometry = PxBoxGeometry_new_1(scaledHalfExtents);
            if (!PxBoxGeometry_isValid(&geometry))
            {
                throw new Exception("Could not generate PhysX box mesh geometry");
            }
            CreateShape((PxGeometry*)&geometry);
            PxShape_setFlags_mut(shape, flags);
            AttachShape(shape);
        }
    }
}
