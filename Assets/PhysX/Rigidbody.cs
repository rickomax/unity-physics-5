using MagicPhysX;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    [DisallowMultipleComponent]
    public unsafe class Rigidbody : MonoBehaviour
    {
        [SerializeField] private bool _kinematic;
        [SerializeField] private bool _useGravity = true;
        [SerializeField] private bool _physicsStatic;

        private readonly List<Collider> _colliders = new List<Collider>();
        private bool _released;

        private Vector3 _kinematicTargetPosition;
        private Quaternion _kinematicTargetRotation;

        public PxRigidDynamic* rigidDynamic { get; private set; }
        public PxRigidStatic* rigidStatic { get; private set; }

        public PxRigidActor* actor => physicsStatic ? (PxRigidActor*)rigidStatic : (PxRigidActor*)rigidDynamic;

        public Vector3 physicsPosition => rigidDynamic != null && isKinematic ? _kinematicTargetPosition : position;
        public Quaternion physicsRotation => rigidDynamic != null && isKinematic ? _kinematicTargetRotation : rotation;

        public bool physicsStatic
        {
            get => _physicsStatic;
            set
            {
                if (_physicsStatic == value)
                {
                    return;
                }

                _physicsStatic = value;
                if (actor != null || rigidDynamic != null || rigidStatic != null)
                {
                    RecreateActor();
                }

                NotifyCollidersShapeDirty();
            }
        }

        public bool isKinematic
        {
            get => !physicsStatic && _kinematic;
            set
            {
                if (_kinematic == value)
                {
                    return;
                }

                _kinematic = value;
                if (physicsStatic || rigidDynamic == null)
                {
                    return;
                }

                PxRigidBody_setRigidBodyFlag_mut((PxRigidBody*)rigidDynamic, PxRigidBodyFlag.Kinematic, value);
                PxRigidBody_setRigidBodyFlag_mut((PxRigidBody*)rigidDynamic, PxRigidBodyFlag.UseKinematicTargetForSceneQueries, value);
                NotifyCollidersShapeDirty();
            }
        }

        public Vector3 position
        {
            get
            {
                if (actor == null)
                {
                    return transform.position;
                }

                return PxRigidActor_getGlobalPose(actor).p;
            }
            set
            {
                if (actor == null)
                {
                    transform.position = value;
                    transform.hasChanged = false;
                    return;
                }

                var pose = PxRigidActor_getGlobalPose(actor);
                SetPositionAndRotation(value, pose.q);
            }
        }

        public Quaternion rotation
        {
            get
            {
                if (actor == null)
                {
                    return transform.rotation;
                }

                return PxRigidActor_getGlobalPose(actor).q;
            }
            set
            {
                if (actor == null)
                {
                    transform.rotation = value;
                    transform.hasChanged = false;
                    return;
                }

                var pose = PxRigidActor_getGlobalPose(actor);
                SetPositionAndRotation(pose.p, value);
            }
        }

        public bool useGravity
        {
            get
            {
                if (rigidDynamic == null)
                {
                    return _useGravity;
                }

                var flags = PxActor_getActorFlags((PxActor*)rigidDynamic);
                return ((int)flags & (int)PxActorFlag.DisableGravity) == 0;
            }
            set
            {
                _useGravity = value;
                if (rigidDynamic == null)
                {
                    return;
                }

                PxActor_setActorFlag_mut((PxActor*)rigidDynamic, PxActorFlag.DisableGravity, !value);
            }
        }

        public Vector3 velocity
        {
            get
            {
                if (rigidDynamic == null)
                {
                    return default;
                }

                return PxRigidDynamic_getLinearVelocity(rigidDynamic);
            }
            set
            {
                if (rigidDynamic == null)
                {
                    throw new NullReferenceException();
                }

                var linearVelocity = (PxVec3)value;
                PxRigidDynamic_setLinearVelocity_mut(rigidDynamic, &linearVelocity, false);
            }
        }

        private void Awake()
        {
            _kinematicTargetPosition = transform.position;
            _kinematicTargetRotation = transform.rotation;
            EnsureActorCreated();
        }

        internal void InitializeAsDummyStatic()
        {
            _physicsStatic = true;
            _kinematic = false;
            _useGravity = false;
            EnsureActorCreated();
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

            SyncTransformFromPhysics();
        }

        private void Update()
        {
            if (actor != null && transform.hasChanged)
            {
                transform.hasChanged = false;
                SyncPhysicsFromTransform();
            }
        }

        public void EnsureActorCreated()
        {
            if (_released || actor != null)
            {
                return;
            }

            RefreshPhysicsTransform();
            var pxTransform = (PxTransform*)Unsafe.AsPointer(ref _physicsTransform);
            if (physicsStatic)
            {
                rigidStatic = PxPhysics_createRigidStatic_mut(PhysicsManager.instance.physics, pxTransform);
                if (rigidStatic != null)
                {
                    PhysicsManager.instance.AddActor((PxActor*)rigidStatic, null);
                }
            }
            else
            {
                rigidDynamic = PxPhysics_createRigidDynamic_mut(PhysicsManager.instance.physics, pxTransform);
                if (rigidDynamic != null)
                {
                    useGravity = _useGravity;
                    isKinematic = _kinematic;
                    PhysicsManager.instance.AddActor((PxActor*)rigidDynamic, null);
                }
            }

            for (var i = 0; i < _colliders.Count; i++)
            {
                _colliders[i]?.AttachExistingShape();
            }
        }

        internal void RegisterCollider(Collider collider)
        {
            if (collider == null || _colliders.Contains(collider))
            {
                return;
            }

            _colliders.Add(collider);
            if (actor != null)
            {
                collider.AttachExistingShape();
            }
        }

        internal void UnregisterCollider(Collider collider)
        {
            if (collider == null)
            {
                return;
            }

            _colliders.Remove(collider);
            collider.DetachExistingShape();
        }

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            EnsureActorCreated();
            if (actor == null)
            {
                throw new NullReferenceException();
            }

            _kinematicTargetPosition = position;
            _kinematicTargetRotation = rotation;

            var pxPosition = (PxVec3)position;
            var pxRotation = (PxQuat)rotation;
            var pxTransform = PxTransform_new_5(&pxPosition, &pxRotation);
            if (rigidDynamic != null && isKinematic)
            {
                PxRigidDynamic_setKinematicTarget_mut(rigidDynamic, &pxTransform);
            }
            else
            {
                PxRigidActor_setGlobalPose_mut(actor, &pxTransform, true);
            }

            transform.position = position;
            transform.rotation = rotation;
            transform.hasChanged = false;
        }

        public void SyncTransformFromPhysics()
        {
            if (actor == null)
            {
                return;
            }

            var pose = PxRigidActor_getGlobalPose(actor);
            transform.position = pose.p;
            transform.rotation = pose.q;
            if (rigidDynamic != null && isKinematic)
            {
                _kinematicTargetPosition = transform.position;
                _kinematicTargetRotation = transform.rotation;
            }

            transform.hasChanged = false;
        }

        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;

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
        }

        private PxTransform _physicsTransform;

        private void RefreshPhysicsTransform()
        {
            var pos = (PxVec3)transform.position;
            var rot = (PxQuat)transform.rotation;
            _physicsTransform = PxTransform_new_5(&pos, &rot);
        }

        private void RecreateActor()
        {
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

            RefreshPhysicsTransform();
            var pxTransform = (PxTransform*)Unsafe.AsPointer(ref _physicsTransform);
            if (physicsStatic)
            {
                rigidStatic = PxPhysics_createRigidStatic_mut(PhysicsManager.instance.physics, pxTransform);
                if (rigidStatic != null)
                {
                    PhysicsManager.instance.AddActor((PxActor*)rigidStatic, null);
                }
            }
            else
            {
                rigidDynamic = PxPhysics_createRigidDynamic_mut(PhysicsManager.instance.physics, pxTransform);
                if (rigidDynamic != null)
                {
                    useGravity = _useGravity;
                    isKinematic = _kinematic;
                    PhysicsManager.instance.AddActor((PxActor*)rigidDynamic, null);
                }
            }

            for (var i = 0; i < _colliders.Count; i++)
            {
                _colliders[i]?.AttachExistingShape();
            }
        }

        private void SyncPhysicsFromTransform()
        {
            if (actor == null)
            {
                return;
            }

            _kinematicTargetPosition = transform.position;
            _kinematicTargetRotation = transform.rotation;

            var pos = (PxVec3)transform.position;
            var rot = (PxQuat)transform.rotation;
            var pose = PxTransform_new_5(&pos, &rot);
            if (rigidDynamic != null && isKinematic)
            {
                PxRigidDynamic_setKinematicTarget_mut(rigidDynamic, &pose);
            }
            else
            {
                PxRigidActor_setGlobalPose_mut(actor, &pose, true);
            }
        }

        private void NotifyCollidersShapeDirty()
        {
            for (var i = 0; i < _colliders.Count; i++)
            {
                _colliders[i]?.SetShapeDirty();
            }
        }
    }
}
