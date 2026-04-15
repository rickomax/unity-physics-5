using MagicPhysX;
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
            var scaledHalfExtents = Vector3.Scale(halfExtents, scale);
            var geometry = PxBoxGeometry_new_1(scaledHalfExtents);
            if (PxBoxGeometry_isValid(&geometry))
            {
                CreateShape((PxGeometry*)&geometry);
                AttachShape(_shape);
            }
        }
    }
}
