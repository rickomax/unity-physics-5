using MagicPhysX;
using System.Runtime.CompilerServices;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    [DefaultExecutionOrder(-8)]
    public abstract unsafe class Collider : MonoBehaviour
    {
        public float density = 10f;
        public float dynamicFriction = 0.6f;
        public float restitution = 0.6f;
        public float staticFriction = 0.6f;

        [SerializeField] private Vector3 _center;

        public Vector3 center
        {
            get => _center;
            set
            {
                if (_center == value)
                {
                    return;
                }
                _center = value;
                RecomputeOffset();
            }
        }

        public bool isTrigger
        {
            get
            {
                if (shape == null)
                {
                    return default;
                }

                var flags = PxShape_getFlags(shape);
                return ((int)flags & (int)PxShapeFlag.TriggerShape) != 0;
            }
            set
            {
                if (shape == null)
                {
                    return;
                }

                PxShape_setFlag_mut(shape, PxShapeFlag.TriggerShape, value);
                PxShape_setFlag_mut(shape, PxShapeFlag.SimulationShape, !value && isActiveAndEnabled);
                PxShape_setFlag_mut(shape, PxShapeFlag.SceneQueryShape, isActiveAndEnabled);
            }
        }

        public Vector3 localPosition
        {
            get => _offset.p;
            set
            {
                _offset.p = (PxVec3)value;
                if (_shape != null)
                {
                    PxShape_setLocalPose_mut(_shape, (PxTransform*)Unsafe.AsPointer(ref _offset));
                }
            }
        }

        public Quaternion localRotation
        {
            get => _offset.q;
            set
            {
                _offset.q = (PxQuat)value;
                if (_shape != null)
                {
                    PxShape_setLocalPose_mut(_shape, (PxTransform*)Unsafe.AsPointer(ref _offset));
                }
            }
        }

        public virtual Vector3 position
        {
            get
            {
                if (actor == null)
                {
                    return default;
                }
                var actorPose = PxRigidActor_getGlobalPose(actor);
                return (Vector3)actorPose.p + (Quaternion)actorPose.q * (Vector3)_offset.p;
            }
            set
            {
                if (actor == null)
                {
                    return;
                }
                var actorPose = PxRigidActor_getGlobalPose(actor);
                localPosition = Quaternion.Inverse(actorPose.q) * (value - (Vector3)actorPose.p);
            }
        }

        public virtual Quaternion rotation
        {
            get
            {
                if (actor == null)
                {
                    return default;
                }

                var actorPose = PxRigidActor_getGlobalPose(actor);
                return (Quaternion)actorPose.q * (Quaternion)_offset.q;
            }
            set
            {
                if (actor == null)
                {
                    return;
                }

                var actorPose = PxRigidActor_getGlobalPose(actor);
                localRotation = Quaternion.Inverse(actorPose.q) * value;
            }
        }

        protected PxMaterial* _material;
        protected PxTransform _offset;
        protected PxShape* _shape;
        protected bool _released;
        protected Rigidbody _attachedRigidbody;

        private int _lastLayer;

        protected virtual bool supportsAttachedRigidbody => true;
        public virtual PxRigidActor* actor => _attachedRigidbody == null ? null : _attachedRigidbody.actor;
        public virtual PxShape* shape => _shape;
        public virtual Rigidbody attachedRigidbody => _attachedRigidbody;
        protected bool isKinematic => _attachedRigidbody?.isKinematic ?? false;
        protected bool isPhysicsStatic => _attachedRigidbody?.isPhysicsStatic ?? false;


        protected virtual void Awake()
        {
            _material = PxPhysics_createMaterial_mut(PhysicsManager.instance.physics, staticFriction, dynamicFriction, restitution);
            _lastLayer = gameObject.layer;
            if (!supportsAttachedRigidbody)
            {
                return;
            }
            _attachedRigidbody = GetComponentInParent<Rigidbody>(true) ?? PhysicsManager.instance.dummyRigidbody;
            _attachedRigidbody.RegisterCollider(this);
            RecomputeOffset();
        }

        protected void RecomputeOffset()
        {
            if (_attachedRigidbody == null)
            {
                return;
            }
            var rbTransform = _attachedRigidbody.transform;
            var shapeWorldPos = transform.TransformPoint(_center);
            var shapeWorldRot = transform.rotation;
            var actorInvRot = Quaternion.Inverse(rbTransform.rotation);
            var localPos = actorInvRot * (shapeWorldPos - rbTransform.position);
            var localRot = actorInvRot * shapeWorldRot;
            var pxPos = (PxVec3)localPos;
            var pxRot = (PxQuat)localRot;
            _offset = PxTransform_new_5(&pxPos, &pxRot);
            if (_shape != null)
            {
                PxShape_setLocalPose_mut(_shape, (PxTransform*)Unsafe.AsPointer(ref _offset));
            }
        }

        private void OnDestroy()
        {
            if (PhysicsManager.isShuttingDown)
            {
                return;
            }

            Release();
        }

        private void OnEnable()
        {
            if (_shape != null)
            {
                PxShape_setFlag_mut(_shape, PxShapeFlag.SimulationShape, !isTrigger);
                PxShape_setFlag_mut(_shape, PxShapeFlag.SceneQueryShape, true);
            }
        }

        private void OnDisable()
        {
            if (_shape != null)
            {
                PxShape_setFlag_mut(_shape, PxShapeFlag.SimulationShape, false);
                PxShape_setFlag_mut(_shape, PxShapeFlag.SceneQueryShape, false);
            }
        }

        protected virtual void Update()
        {
            if (_shape != null && gameObject.layer != _lastLayer)
            {
                SetupFilterData(_shape);
                _lastLayer = gameObject.layer;
            }
        }

        public virtual void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            DestroyShape();

            if (_attachedRigidbody != null)
            {
                _attachedRigidbody.UnregisterCollider(this);
                _attachedRigidbody = null;
            }

            _material = null;
        }

        protected virtual Vector3 GetPhysicsScale() => transform.lossyScale;

        public virtual PxTransform GetQueryPose(Vector3 worldPosition, Quaternion worldRotation)
        {
            var shapeWorldPos = worldPosition + worldRotation * (Vector3)_offset.p;
            var shapeWorldRot = worldRotation * (Quaternion)_offset.q;
            var pxPos = (PxVec3)shapeWorldPos;
            var pxRot = (PxQuat)shapeWorldRot;
            return PxTransform_new_5(&pxPos, &pxRot);
        }

        public void NotifyAttachedRigidbodyReleased(Rigidbody rigidbody)
        {
            if (_attachedRigidbody == rigidbody)
            {
                _attachedRigidbody = null;
            }
        }

        public void AttachExistingShape()
        {
            if (_shape != null)
            {
                AttachShape(_shape);
            }
        }

        public void DetachExistingShape()
        {
            if (_shape != null)
            {
                DetachShape(_shape);
            }
        }

        protected void AttachShape(PxShape* shapeToAttach)
        {
            if (actor == null || shapeToAttach == null)
            {
                return;
            }
            PxShape_setLocalPose_mut(shapeToAttach, (PxTransform*)Unsafe.AsPointer(ref _offset));
            PxRigidActor_attachShape_mut(actor, shapeToAttach);
            PhysicsManager.instance.RegisterShape(shapeToAttach, this);
        }

        protected void DetachShape(PxShape* shapeToDetach)
        {
            if (shapeToDetach == null)
            {
                return;
            }
            var actor = PxShape_getActor(shapeToDetach);
            if (actor != null)
            {
                PxRigidActor_detachShape_mut(actor, shapeToDetach, true);
            }
            PhysicsManager.instance.UnregisterShape(shapeToDetach);
        }

        protected void CreateShape(PxGeometry* geometry)
        {
            PxShapeFlags flags = PxShapeFlags.SimulationShape | PxShapeFlags.SceneQueryShape;
            _shape = PxPhysics_createShape_mut(PhysicsManager.instance.physics, geometry, _material, true, flags);
            if (_shape != null)
            {
                PxShape_setLocalPose_mut(_shape, (PxTransform*)Unsafe.AsPointer(ref _offset));
                SetupFilterData(_shape);
            }
            else
            {
                Debug.LogError("Could not create PhysX shape");
            }
        }

        protected void DestroyShape()
        {
            if (_shape == null)
            {
                return;
            }
            DetachShape(_shape);
            PxShape_release_mut(_shape);
            _shape = null;
        }

        public virtual void RebuildShape()
        {
        }

        protected void SetupFilterData(PxShape* targetShape)
        {
            var filterData = PxFilterData_new_2((uint)(1 << gameObject.layer), 0, 0, 0);
            PxShape_setQueryFilterData_mut(targetShape, &filterData);
            PxShape_setSimulationFilterData_mut(targetShape, &filterData);
        }
    }
}
