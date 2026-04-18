using Codice.Client.BaseCommands;
using MagicPhysX;
using System.Collections.Generic;
using System.Text;
using Unity.VectorGraphics;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    [DefaultExecutionOrder(-9)]
    [DisallowMultipleComponent]
    public unsafe class Rigidbody : MonoBehaviour
    {
        private readonly List<Collider> _colliders = new List<Collider>();
        private Vector3 _kinematicTargetPosition;
        private Quaternion _kinematicTargetRotation;
        private bool _isReleased;
        private bool _isPhysicsStatic;

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
                if (isKinematic)
                {
                    PxRigidDynamic_setKinematicTarget_mut(rigidDynamic, &pxTransform);
                    transform.position = value;

                }
                else
                {
                    PxRigidActor_setGlobalPose_mut(actor, &pxTransform, true);
                }
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
                if (isKinematic)
                {
                    PxRigidDynamic_setKinematicTarget_mut(rigidDynamic, &pxTransform);
                    transform.rotation = value;

                }
                else
                {
                    PxRigidActor_setGlobalPose_mut(actor, &pxTransform, true);
                }
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
        public string physicsName
        {
            set
            {
                if (actor == null)
                {
                    return;
                }
                fixed (byte* bytes = Encoding.UTF8.GetBytes(value + "\0"))
                {
                    PxActor_setName_mut((PxActor*)actor, bytes);
                }
            }
        }

        public void InitializeAsDummyStatic()
        {
            _isPhysicsStatic = true;
            isKinematic = false;
            useGravity = false;
        }

        private void Awake()
        {
            _kinematicTargetPosition = transform.position;
            _kinematicTargetRotation = transform.rotation;
            CreateActor();
        }

        private void CreateActor()
        {
            var pxPosition = (PxVec3)transform.position;
            var pxRotation = (PxQuat)transform.rotation;
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
                    PhysicsManager.instance.AddActor((PxActor*)rigidDynamic, null);
                }
            }
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

            if (isKinematic)
            {
                _kinematicTargetPosition = transform.position;
                _kinematicTargetRotation = transform.rotation;
            }
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
        }
    }
}
