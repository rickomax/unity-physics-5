using MagicPhysX;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    [DefaultExecutionOrder(-9)]
    [DisallowMultipleComponent]
    public unsafe class Rigidbody : MonoBehaviour
    {
        public const uint DefaultPositionIterations = 8;
        public const uint DefaultVelocityIterations = 2;

        [SerializeField] private float _density = 1f;

        private readonly List<Collider> _colliders = new List<Collider>();
        private Vector3 _kinematicTargetPosition;
        private Quaternion _kinematicTargetRotation;
        private bool _isReleased;
        private bool _isPhysicsStatic;

        public float sleepThreshold
        {
            get => rigidDynamic == null ? 0f : PxRigidDynamic_getSleepThreshold(rigidDynamic);
            set { if (rigidDynamic != null) PxRigidDynamic_setSleepThreshold_mut(rigidDynamic, value); }
        }

        public float wakeCounter
        {
            set { if (rigidDynamic != null) PxRigidDynamic_setWakeCounter_mut(rigidDynamic, value); }
        }

        public float stabilizationThreshold
        {
            set { if (rigidDynamic != null) PxRigidDynamic_setStabilizationThreshold_mut(rigidDynamic, value); } 
        }

        public float density
        {
            get => _density;
            set
            {
                if (Mathf.Approximately(_density, value))
                {
                    return;
                }
                _density = value;
                RecomputeMassAndInertia();
            }
        }

        public bool hasDynamicActor => rigidDynamic != null;
        public bool hasStaticActor => rigidStatic != null;
        public bool isReleased => _isReleased;

        public bool isPhysicsStatic
        {
            get
            {
                return _isPhysicsStatic;
            }
            set
            {
                if (_isPhysicsStatic == value)
                {
                    return;
                }
                _isPhysicsStatic = value;
                RebuildActor();
            }
        }

        public bool enableCcd
        {
            get
            {
                if (!hasDynamicActor)
                {
                    return false;
                }
                var flags = PxRigidBody_getRigidBodyFlags((PxRigidBody*)rigidDynamic);
                return ((int)flags & (int)PxRigidBodyFlag.EnableCcd) != 0;
            }
            set
            {
                if (!hasDynamicActor)
                {
                    return;
                }
                PxRigidBody_setRigidBodyFlag_mut((PxRigidBody*)rigidDynamic, PxRigidBodyFlag.EnableCcd, value);
                PxRigidBody_setRigidBodyFlag_mut((PxRigidBody*)rigidDynamic, PxRigidBodyFlag.EnableSpeculativeCcd, value);
            }
        }

        public float angularDamping
        {
            get
            {
                if (rigidDynamic == null)
                {
                    return default;
                }
                return PxRigidBody_getAngularDamping((PxRigidBody*)rigidDynamic);
            }
            set
            {
                if (rigidDynamic == null)
                {
                    return;
                }
                PxRigidBody_setAngularDamping_mut((PxRigidBody*)rigidDynamic, value);
            }
        }

        public float linearDamping
        {
            get
            {
                if (rigidDynamic == null)
                {
                    return default;
                }
                return PxRigidBody_getLinearDamping((PxRigidBody*)rigidDynamic);
            }
            set
            {
                if (rigidDynamic == null)
                {
                    return;
                }
                PxRigidBody_setLinearDamping_mut((PxRigidBody*)rigidDynamic, value);
            }
        }

        public PxRigidDynamic* rigidDynamic { get; private set; }
        public PxRigidStatic* rigidStatic { get; private set; }
        public PxRigidActor* actor => hasDynamicActor ? (PxRigidActor*)rigidDynamic : (PxRigidActor*)rigidStatic;

        public bool isKinematic
        {
            get
            {
                if (!hasDynamicActor)
                {
                    return false;
                }
                var flags = PxRigidBody_getRigidBodyFlags((PxRigidBody*)rigidDynamic);
                return ((int)flags & (int)PxRigidBodyFlag.Kinematic) != 0;
            }
            set
            {
                if (!hasDynamicActor)
                {
                    return;
                }
                PxRigidBody_setRigidBodyFlag_mut((PxRigidBody*)rigidDynamic, PxRigidBodyFlag.Kinematic, value);
                PxRigidBody_setRigidBodyFlag_mut((PxRigidBody*)rigidDynamic, PxRigidBodyFlag.UseKinematicTargetForSceneQueries, value);
            }
        }

        public Vector3 position
        {
            get => isKinematic ? _kinematicTargetPosition : actor == null ? default : (Vector3)PxRigidActor_getGlobalPose(actor).p;
            set
            {
                if (actor == null)
                {
                    return;
                }
                var pxTransform = PxRigidActor_getGlobalPose(actor);
                pxTransform.p = (PxVec3)value;
                if (isKinematic && enabled)
                {
                    PxRigidDynamic_setKinematicTarget_mut(rigidDynamic, &pxTransform);
                    transform.position = value;

                }
                else
                {
                    PxRigidActor_setGlobalPose_mut(actor, &pxTransform, true);
                }
                _kinematicTargetPosition = value;
            }
        }

        public Quaternion rotation
        {
            get => isKinematic ? _kinematicTargetRotation : actor == null ? default : (Quaternion)PxRigidActor_getGlobalPose(actor).q;
            set
            {
                if (actor == null)
                {
                    return;
                }
                var pxTransform = PxRigidActor_getGlobalPose(actor);
                pxTransform.q = (PxQuat)value;
                if (isKinematic && enabled)
                {
                    PxRigidDynamic_setKinematicTarget_mut(rigidDynamic, &pxTransform);
                    transform.rotation = value;

                }
                else
                {
                    PxRigidActor_setGlobalPose_mut(actor, &pxTransform, true);
                }
                _kinematicTargetRotation = value;
            }
        }

        public bool useGravity
        {
            get
            {
                if (!hasDynamicActor)
                {
                    return default;
                }
                return ((int)PxActor_getActorFlags((PxActor*)rigidDynamic) & (int)PxActorFlag.DisableGravity) == 0;
            }
            set
            {
                if (!hasDynamicActor)
                {
                    return;
                }
                PxActor_setActorFlag_mut((PxActor*)rigidDynamic, PxActorFlag.DisableGravity, !value);
            }
        }

        public Vector3 velocity
        {
            get => hasDynamicActor ? (Vector3)PxRigidDynamic_getLinearVelocity(rigidDynamic) : default;
            set
            {
                if (!hasDynamicActor)
                {
                    return;
                }
                var v = (PxVec3)value;
                PxRigidDynamic_setLinearVelocity_mut(rigidDynamic, &v, false);
            }
        }

        private IntPtr _physicsNamePtr;

        public string physicsName
        {
            set
            {
                if (actor == null)
                {
                    return;
                }

                if (_physicsNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_physicsNamePtr);
                    _physicsNamePtr = IntPtr.Zero;
                }

                if (string.IsNullOrEmpty(value))
                {
                    PxActor_setName_mut((PxActor*)actor, null);
                    return;
                }

                var byteCount = Encoding.UTF8.GetByteCount(value);
                _physicsNamePtr = Marshal.AllocHGlobal(byteCount + 1);

                var span = new Span<byte>((void*)_physicsNamePtr, byteCount + 1);
                Encoding.UTF8.GetBytes(value, span);
                span[byteCount] = 0;

                PxActor_setName_mut((PxActor*)actor, (byte*)_physicsNamePtr);
            }
        }

        private void Awake()
        {
            CreateActor();
        }

        private void CreateActor()
        {
            var pxPosition = (PxVec3)Vector3.zero;
            var pxRotation = (PxQuat)Quaternion.identity;
            var pxTransform = PxTransform_new_5(&pxPosition, &pxRotation);
            if (_isPhysicsStatic)
            {
                rigidStatic = PxPhysics_createRigidStatic_mut(PhysicsManager.instance.physics, &pxTransform);
                if (rigidStatic != null)
                {
                    PhysicsManager.instance.AddActor((PxActor*)rigidStatic, null);
                }
            }
            else
            {
                rigidDynamic = PxPhysics_createRigidDynamic_mut(PhysicsManager.instance.physics, &pxTransform);
                if (rigidDynamic != null)
                {
                    PxRigidDynamic_setSolverIterationCounts_mut(rigidDynamic, DefaultPositionIterations, DefaultVelocityIterations);
                    PhysicsManager.instance.AddActor((PxActor*)rigidDynamic, null);
                }
            }
        }

        public void RecomputeMassAndInertia()
        {
            if (rigidDynamic == null || _density <= 0f)
            {
                return;
            }
            uint shapeCount = PxRigidActor_getNbShapes((PxRigidActor*)rigidDynamic);
            if (shapeCount == 0)
            {
                return;
            }
            var density = _density;
            PxRigidBodyExt_updateMassAndInertia_1((PxRigidBody*)rigidDynamic, density, null, false);
        }

        private void RebuildActor()
        {
            if (!Application.isPlaying)
            {
                return;
            }
            for (var i = 0; i < _colliders.Count; i++)
            {
                _colliders[i]?.DetachExistingShape();
            }
            if (actor != null)
            {
                PhysicsManager.instance.RemoveCollider((PxActor*)actor);
                PxRigidActor_release_mut(actor);
            }
            rigidDynamic = null;
            rigidStatic = null;
            CreateActor();
            for (var i = 0; i < _colliders.Count; i++)
            {
                _colliders[i]?.AttachExistingShape();
            }
        }

        private void OnEnable()
        {
            if (actor != null)
            {
                PxActor_setActorFlag_mut((PxActor*)actor, PxActorFlag.DisableSimulation, false);
            }
        }

        private void OnDisable()
        {
            if (actor != null)
            {
                PxActor_setActorFlag_mut((PxActor*)actor, PxActorFlag.DisableSimulation, true);
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

        private void FixedUpdate()
        {
            if (rigidDynamic == null)
            {
                return;
            }
            var pose = PxRigidActor_getGlobalPose((PxRigidActor*)rigidDynamic);
            transform.SetPositionAndRotation(pose.p, pose.q);

            //if (isKinematic)
            //{
            //    _kinematicTargetPosition = transform.position;
            //    _kinematicTargetRotation = transform.rotation;
            //}
        }

        public void RegisterCollider(Collider collider)
        {
            if (collider == null || _colliders.Contains(collider))
            {
                return;
            }
            _colliders.Add(collider);
        }

        public void UnregisterCollider(Collider collider)
        {
            _colliders.Remove(collider);
        }

        public void Release()
        {
            if (_isReleased)
            {
                return;
            }
            _isReleased = true;
            for (var i = _colliders.Count - 1; i >= 0; i--)
            {
                var collider = _colliders[i];
                if (collider == null)
                {
                    continue;
                }
                collider.DetachExistingShape();
                collider.NotifyAttachedRigidbodyReleased(this);
            }
            _colliders.Clear();
            if (actor != null)
            {
                PhysicsManager.instance.RemoveCollider((PxActor*)actor);
                PxRigidActor_release_mut(actor);
            }
            rigidDynamic = null;
            rigidStatic = null;
            if (_physicsNamePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_physicsNamePtr);
                _physicsNamePtr = IntPtr.Zero;
            }
        }

        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            var pxForce = (PxVec3)force;
            switch (mode)
            {
                case ForceMode.Force:
                    PxRigidBody_addForce_mut((PxRigidBody*)rigidDynamic, &pxForce, PxForceMode.Force, true);
                    break;
                case ForceMode.Acceleration:
                    PxRigidBody_addForce_mut((PxRigidBody*)rigidDynamic, &pxForce, PxForceMode.Acceleration, true);
                    break;
                case ForceMode.Impulse:
                    PxRigidBody_addForce_mut((PxRigidBody*)rigidDynamic, &pxForce, PxForceMode.Impulse, true);
                    break;
                case ForceMode.VelocityChange:
                    PxRigidBody_addForce_mut((PxRigidBody*)rigidDynamic, &pxForce, PxForceMode.VelocityChange, true);
                    break;
            }
        }
    }
}
