using MagicPhysX;
using System.Runtime.CompilerServices;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public abstract unsafe class Collider : MonoBehaviour
    {
        public Vector3 center;
        public float density = 10f;
        public float dynamicFriction = 0.6f;
        public bool isTrigger;
        public float restitution = 0.6f;
        public float staticFriction = 0.6f;

        protected PxMaterial* _material;
        protected PxTransform _offset;
        protected PxShape* _shape;
        protected bool _released;
        protected Rigidbody _attachedRigidbody;

        protected Transform _transform;
        private Transform _rigidbodyTransform;

        private Vector3 _lastLocalPosition;
        private Quaternion _lastLocalRotation;
        private Vector3 _lastCenter;

        private int _lastLayer;
        private bool _lastEnabled;

        protected virtual bool supportsAttachedRigidbody => true;

        public virtual PxRigidActor* actor => _attachedRigidbody == null ? null : _attachedRigidbody.actor;
        public virtual PxShape* shape => _shape;
        public virtual Rigidbody attachedRigidbody => _attachedRigidbody;
        public bool usesDummyRigidbody => _attachedRigidbody != null && _attachedRigidbody == PhysicsManager.instance?.dummyRigidbody;

        protected bool hasAttachedRigidbody => _attachedRigidbody != null;
        protected bool isRootOfAttachedRigidbody => _attachedRigidbody != null && _attachedRigidbody.transform == transform;
        protected bool actorIsStatic => _attachedRigidbody?.physicsStatic ?? false;
        protected bool actorIsKinematic => _attachedRigidbody?.isKinematic ?? false;

        protected virtual void Awake()
        {
            _transform = transform;
            _material = PxPhysics_createMaterial_mut(PhysicsManager.instance.physics, staticFriction, dynamicFriction, restitution);
            _lastLayer = gameObject.layer;
            _lastEnabled = enabled;
            ResolveAttachedRigidbody();
            RefreshShapeOffset();
            SeedOffsetCache();
        }

        private void OnDestroy()
        {
            if (PhysicsManager.isShuttingDown)
            {
                return;
            }

            Release();
        }

        protected virtual void Update()
        {
            if (_shape != null)
            {
                if (_lastEnabled != enabled)
                {
                    ApplyShapeFlags(_shape);
                    _lastEnabled = enabled;
                }

                if (gameObject.layer != _lastLayer)
                {
                    SetupFilterData(_shape);
                    _lastLayer = gameObject.layer;
                }
            }

            if (hasAttachedRigidbody)
            {
                UpdateShapeLocalPose(false);
            }
        }

        public virtual void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            if (_attachedRigidbody != null)
            {
                _attachedRigidbody.UnregisterCollider(this);
                _attachedRigidbody = null;
            }

            if (_shape != null)
            {
                DetachShape(_shape);
                PxShape_release_mut(_shape);
                _shape = null;
            }

            if (_material != null)
            {
                _material = null;
            }
        }

        protected virtual Vector3 GetPhysicsScale()
        {
            return _transform.lossyScale;
        }

        private void SeedOffsetCache()
        {
            _lastLocalPosition = _transform.localPosition;
            _lastLocalRotation = _transform.localRotation;
            _lastCenter = center;
        }

        private void InvalidateOffsetCache()
        {
            _lastLocalPosition = _transform.localPosition + Vector3.one;
        }

        internal virtual PxTransform GetQueryPose(Vector3 worldPosition, Quaternion worldRotation)
        {
            var scaledCenter = Vector3.Scale(center, GetPhysicsScale());
            var shapePosition = worldPosition + worldRotation * scaledCenter;
            var pxPosition = (PxVec3)shapePosition;
            var pxRotation = (PxQuat)worldRotation;
            return PxTransform_new_5(&pxPosition, &pxRotation);
        }

        internal void NotifyAttachedRigidbodyReleased(Rigidbody rigidbody)
        {
            if (_attachedRigidbody == rigidbody)
            {
                _attachedRigidbody = null;
            }
        }

        internal void AttachExistingShape()
        {
            if (_shape != null)
            {
                AttachShape(_shape);
            }
        }

        internal void DetachExistingShape()
        {
            if (_shape != null)
            {
                DetachShape(_shape);
            }
        }

        protected void CreateActor()
        {
            if (!supportsAttachedRigidbody)
            {
                return;
            }

            ResolveAttachedRigidbody();

            if (_attachedRigidbody != null)
            {
                _attachedRigidbody.EnsureActorCreated();
            }
        }

        protected void AttachShape(PxShape* shapeToAttach)
        {
            if (actor == null || shapeToAttach == null)
            {
                return;
            }

            if (_attachedRigidbody != null)
            {
                RefreshShapeOffset();
                PxShape_setLocalPose_mut(shapeToAttach, (PxTransform*)Unsafe.AsPointer(ref _offset));
            }

            PxRigidActor_attachShape_mut(actor, shapeToAttach);
            PhysicsManager.instance.RegisterShape(shapeToAttach, this);
        }

        protected void DetachShape(PxShape* shapeToDetach)
        {
            if (shapeToDetach == null)
            {
                return;
            }

            PhysicsManager.instance.UnregisterShape(shapeToDetach);
            if (actor != null)
            {
                PxRigidActor_detachShape_mut(actor, shapeToDetach, true);
            }
        }

        protected void CreateShape(PxGeometry* geometry)
        {
            RefreshShapeOffset();

            PxShapeFlags flags = isTrigger
                ? PxShapeFlags.TriggerShape
                : PxShapeFlags.SimulationShape | PxShapeFlags.SceneQueryShape;

            _shape = PxPhysics_createShape_mut(PhysicsManager.instance.physics, geometry, _material, true, flags);
            if (_shape != null)
            {
                PxShape_setLocalPose_mut(_shape, (PxTransform*)Unsafe.AsPointer(ref _offset));
                SetupFilterData(_shape);
                ApplyShapeFlags(_shape);
            }
            else
            {
                Debug.LogError("Could not create PhysX shape");
            }
        }

        protected void SetupFilterData(PxShape* targetShape)
        {
            var filterData = PxFilterData_new_2((uint)(1 << gameObject.layer), 0, 0, 0);
            PxShape_setQueryFilterData_mut(targetShape, &filterData);
            PxShape_setSimulationFilterData_mut(targetShape, &filterData);
        }

        protected void UpdateShapeLocalPose(bool force)
        {
            if (_shape == null || _attachedRigidbody == null)
            {
                return;
            }
            if (!force
                && center == _lastCenter
                && Approximately(_transform.localPosition, _lastLocalPosition)
                && Approximately(_transform.localRotation, _lastLocalRotation))
            {
                return;
            }
            SeedOffsetCache();
            RefreshShapeOffset();
            PxShape_setLocalPose_mut(_shape, (PxTransform*)Unsafe.AsPointer(ref _offset));
        }

        protected void RefreshShapeOffset()
        {
            var rbTransform = _rigidbodyTransform != null ? _rigidbodyTransform : _transform;
            var shapeWorldPosition = _transform.TransformPoint(center);
            var shapeWorldRotation = _transform.rotation;
            var actorInvRotation = Quaternion.Inverse(rbTransform.rotation);
            var localPosition = actorInvRotation * (shapeWorldPosition - rbTransform.position);
            var localRotation = actorInvRotation * shapeWorldRotation;
            var pxPosition = (PxVec3)localPosition;
            var pxRotation = (PxQuat)localRotation;
            _offset = PxTransform_new_5(&pxPosition, &pxRotation);
        }

        private void ResolveAttachedRigidbody()
        {
            if (!supportsAttachedRigidbody)
            {
                return;
            }

            var resolved = GetComponentInParent<Rigidbody>();
            if (resolved == null && PhysicsManager.instance != null)
            {
                resolved = PhysicsManager.instance.dummyRigidbody;
            }

            if (_attachedRigidbody == resolved)
            {
                return;
            }

            if (_attachedRigidbody != null)
            {
                _attachedRigidbody.UnregisterCollider(this);
            }

            _attachedRigidbody = resolved;
            _rigidbodyTransform = resolved != null ? resolved.transform : null;

            if (_attachedRigidbody != null)
            {
                _attachedRigidbody.RegisterCollider(this);
                InvalidateOffsetCache(); // force recompute against the new rigidbody next frame
            }
        }

        private void ApplyShapeFlags(PxShape* targetShape)
        {
            var shapeEnabled = enabled;
            if (isTrigger)
            {
                PxShape_setFlag_mut(targetShape, PxShapeFlag.TriggerShape, shapeEnabled);
                PxShape_setFlag_mut(targetShape, PxShapeFlag.SimulationShape, false);
                PxShape_setFlag_mut(targetShape, PxShapeFlag.SceneQueryShape, false);
            }
            else
            {
                PxShape_setFlag_mut(targetShape, PxShapeFlag.TriggerShape, false);
                PxShape_setFlag_mut(targetShape, PxShapeFlag.SimulationShape, shapeEnabled);
                PxShape_setFlag_mut(targetShape, PxShapeFlag.SceneQueryShape, shapeEnabled);
            }
        }

        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude <= 0.000001f;
        }

        private static bool Approximately(Quaternion a, Quaternion b)
        {
            return Mathf.Abs(Quaternion.Dot(a, b)) >= 0.999999f;
        }
    }
}