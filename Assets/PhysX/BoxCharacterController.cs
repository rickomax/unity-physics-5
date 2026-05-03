using AOT;
using MagicPhysX;
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    [DefaultExecutionOrder(-7)]
    public unsafe class BoxCharacterController : Collider
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnControllerHitDelegate(PxControllersHit* hit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnDestructorDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnObstacleHitDelegate(PxControllerObstacleHit* hit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OnShapeHitDelegate(PxControllerShapeHit* hit);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate PxControllerBehaviorFlags GetBehaviorFlagsShapeDelegate(PxController* sourceController, PxShape* shape, PxActor* actor);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate PxControllerBehaviorFlags GetBehaviorFlagsControllerDelegate(PxController* sourceController, PxController* otherController);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate PxControllerBehaviorFlags GetBehaviorFlagsObstacleDelegate(PxController* sourceController, PxObstacle* obstacle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate PxQueryHitType CctQueryPreFilterDelegate(PxRigidActor* rigidActor, PxFilterData* filterData, PxShape* shape, uint hitFlags, void* userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool CctControllerFilterDelegate(PxController* controllerA, PxController* controllerB, void* userData);

        public delegate void ControllerHitDelegate(BoxCharacterController controller, ControllerShapeHit hit);

        public static ControllerHitDelegate OnPhysXControllerColliderHit;

        private static readonly OnControllerHitDelegate _onControllerHit = OnControllerHit;
        private static readonly OnDestructorDelegate _onDestructor = OnDestructor;
        private static readonly OnObstacleHitDelegate _onObstacleHit = OnObstacleHit;
        private static readonly OnShapeHitDelegate _onShapeHit = OnShapeHit;
        private static readonly GetBehaviorFlagsShapeDelegate _getBehaviorFlagsShape = GetBehaviorFlagsShape;
        private static readonly GetBehaviorFlagsControllerDelegate _getBehaviorFlagsController = GetBehaviorFlagsController;
        private static readonly GetBehaviorFlagsObstacleDelegate _getBehaviorFlagsObstacle = GetBehaviorFlagsObstacle;
        private static readonly CctQueryPreFilterDelegate _cctQueryPreFilter = CctQueryPreFilter;
        private static readonly CctControllerFilterDelegate _cctControllerFilter = CctControllerFilter;
        private static readonly IntPtr _cctQueryPreFilterPtr = Marshal.GetFunctionPointerForDelegate(_cctQueryPreFilter);
        private static readonly IntPtr _cctControllerFilterPtr = Marshal.GetFunctionPointerForDelegate(_cctControllerFilter);
        private static readonly ControllerShapeHit _cachedControllerShapeHit = new ControllerShapeHit();

        [SerializeField] private float _invisibleWallHeight;
        [SerializeField] private float _maxJumpHeight;
        [SerializeField] private float _minDist = 0.001f;
        [SerializeField] private float _scaleCoeff = 0.999f;
        [SerializeField] private float _volumeGrowth = 1.5f;
        [SerializeField] private PxControllerNonWalkableMode _nonWalkableMode = PxControllerNonWalkableMode.PreventClimbing;

        private PxController* _controller;
        private PxBoxControllerDesc* _controllerDesc;
        private IntPtr _callbackInfoPtr;
        private IntPtr _behaviorCallbackInfoPtr;
        private PxQueryFilterCallback* _queryFilterCallback;
        private PxControllerFilterCallback* _controllerFilterCallback;
        private float _lastMoveTime;
        private bool _firstMove = true;
        private Vector3 _computedVelocity;
        private int _lastLayer;
        private int _cachedCollisionMask;

        protected override bool supportsAttachedRigidbody => false;


        public Vector3 upDirection
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }
                return PxController_getUpDirection(_controller);
            }

            set
            {
                if (_controller == null)
                {
                    return;
                }
                var pxVec3 = (PxVec3)value;
                PxController_setUpDirection_mut(_controller, &pxVec3);
            }
        }

        public override PxRigidActor* actor
        {
            get
            {
                if (_controller == null)
                {
                    return null;
                }
                return (PxRigidActor*)PxController_getActor(_controller);
            }
        }

        public float halfForwardExtent
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }
                return PxBoxController_getHalfForwardExtent((PxBoxController*)_controller);
            }
            set {
                if (_controller == null)
                {
                    return;
                }
                PxBoxController_setHalfForwardExtent_mut((PxBoxController*)_controller, value);
            }
        }

        public float halfHeight
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }
                return PxBoxController_getHalfHeight((PxBoxController*)_controller);
            }
            set
            {
                if (_controller == null)
                {
                    return;
                }
                PxBoxController_setHalfHeight_mut((PxBoxController*)_controller, value);
            }
        }

        public float halfSideExtent
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }
                return PxBoxController_getHalfSideExtent((PxBoxController*)_controller);
            }
            set
            {
                if (_controller == null)
                {
                    return;
                }
                PxBoxController_setHalfSideExtent_mut((PxBoxController*)_controller, value);
            }
        }

        public float contactOffset
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }
                return PxController_getContactOffset(_controller);
            }
            set
            {
                if (_controller == null)
                {
                    return;
                }
                PxController_setContactOffset_mut(_controller, value);
            }
        }

        public float slopeLimit
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }
                return PxController_getSlopeLimit(_controller) * Mathf.Rad2Deg;
            }
            set
            {
                if (_controller == null)
                {
                    return;
                }
                PxController_setSlopeLimit_mut(_controller, value * Mathf.Deg2Rad);
            }
        }

        public float stepOffset
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }
                return PxController_getStepOffset(_controller);
            }
            set
            {
                if (_controller == null)
                {
                    return;
                }
                PxController_setStepOffset_mut(_controller, value);
            }
        }

        public float invisibleWallHeight { get => _invisibleWallHeight; set => _invisibleWallHeight = value; }
        public float maxJumpHeight { get => _maxJumpHeight; set => _maxJumpHeight = value; }
        public float minDist { get => _minDist; set => _minDist = value; }
        public float scaleCoeff { get => _scaleCoeff; set => _scaleCoeff = value; }
        public float volumeGrowth { get => _volumeGrowth; set => _volumeGrowth = value; }
        public bool isGrounded { get; private set; }
        public PxControllerNonWalkableMode nonWalkableMode { get => _nonWalkableMode; set => _nonWalkableMode = value; }

        public Vector3 footPosition
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }

                var foot = PxController_getFootPosition(_controller);
                return new Vector3((float)foot.x, (float)foot.y, (float)foot.z);
            }
            set
            {
                if (_controller == null)
                {
                    return;
                }

                var foot = PxExtendedVec3_new_1(value.x, value.y, value.z);
                PxController_setFootPosition_mut(_controller, &foot);
            }
        }

        public override Vector3 position
        {
            get
            {
                if (_controller == null)
                {
                    return default;
                }

                var pos = PxController_getPosition(_controller);
                return new Vector3((float)pos->x, (float)pos->y, (float)pos->z);
            }
            set
            {
                if (_controller == null || !enabled)
                {
                    return;
                }

                if (!IsFinite(value))
                {
                    Debug.LogWarning($"[BoxCharacterController] Ignoring non-finite position {value} on '{name}'.", this);
                    return;
                }

                var pos = PxExtendedVec3_new_1(value.x, value.y, value.z);
                PxController_setPosition_mut(_controller, &pos);
                transform.position = position;

            }
        }

        public override Quaternion rotation
        {
            get => transform.rotation;
            set => transform.rotation = value;
        }

        public Vector3 velocity => _computedVelocity;

        private IntPtr _physicsNamePtr;


        public override string physicsName
        {
            set
            {
                if (actor == null || shape == null)
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
                    PxShape_setName_mut((PxShape*)shape, null);
                    return;
                }

                var byteCount = Encoding.UTF8.GetByteCount(value);
                _physicsNamePtr = Marshal.AllocHGlobal(byteCount + 1);

                var span = new Span<byte>((void*)_physicsNamePtr, byteCount + 1);
                Encoding.UTF8.GetBytes(value, span);
                span[byteCount] = 0;

                PxActor_setName_mut((PxActor*)actor, (byte*)_physicsNamePtr);
                PxShape_setName_mut((PxShape*)shape, (byte*)_physicsNamePtr);
            }
        }

        protected override void Awake()
        {
            base.Awake();
            var pxPosition = (PxVec3)transform.position;
            _controllerDesc = PxBoxControllerDesc_new_alloc();
            PxBoxControllerDesc_setToDefault_mut(_controllerDesc);
            _controllerDesc->position.x = pxPosition.x;
            _controllerDesc->position.y = pxPosition.y;
            _controllerDesc->position.z = pxPosition.z;
            _controllerDesc->invisibleWallHeight = _invisibleWallHeight;
            _controllerDesc->maxJumpHeight = _maxJumpHeight;
            _controllerDesc->density = 10f;
            _controllerDesc->scaleCoeff = _scaleCoeff;
            _controllerDesc->volumeGrowth = _volumeGrowth;
            _controllerDesc->nonWalkableMode = _nonWalkableMode;
            _controllerDesc->material = _material;

            var callbackInfo = new ControllerCallbackInfo();
            callbackInfo.controllerObstacleHitCallback = (delegate* unmanaged[Cdecl]<PxControllerObstacleHit*, void>)Marshal.GetFunctionPointerForDelegate(_onObstacleHit);
            callbackInfo.controllerShapeHitCallback = (delegate* unmanaged[Cdecl]<PxControllerShapeHit*, void>)Marshal.GetFunctionPointerForDelegate(_onShapeHit);
            callbackInfo.controllersHitCallback = (delegate* unmanaged[Cdecl]<PxControllersHit*, void>)Marshal.GetFunctionPointerForDelegate(_onControllerHit);
            _callbackInfoPtr = Marshal.AllocHGlobal(sizeof(ControllerCallbackInfo));
            *(ControllerCallbackInfo*)_callbackInfoPtr = callbackInfo;
            _controllerDesc->reportCallback = create_user_controller_hit_report((ControllerCallbackInfo*)_callbackInfoPtr);

            var behaviorCallbackInfo = new ControllerBehaviorCallbackInfo();
            behaviorCallbackInfo.getBehaviorFlagsShape = (delegate* unmanaged[Cdecl]<PxController*, PxShape*, PxActor*, PxControllerBehaviorFlags>)Marshal.GetFunctionPointerForDelegate(_getBehaviorFlagsShape);
            behaviorCallbackInfo.getBehaviorFlagsController = (delegate* unmanaged[Cdecl]<PxController*, PxController*, PxControllerBehaviorFlags>)Marshal.GetFunctionPointerForDelegate(_getBehaviorFlagsController);
            behaviorCallbackInfo.getBehaviorFlagsObstacle = (delegate* unmanaged[Cdecl]<PxController*, PxObstacle*, PxControllerBehaviorFlags>)Marshal.GetFunctionPointerForDelegate(_getBehaviorFlagsObstacle);
            _behaviorCallbackInfoPtr = Marshal.AllocHGlobal(sizeof(ControllerBehaviorCallbackInfo));
            *(ControllerBehaviorCallbackInfo*)_behaviorCallbackInfoPtr = behaviorCallbackInfo;
            _controllerDesc->behaviorCallback = create_controller_behavior_callback((ControllerBehaviorCallbackInfo*)_behaviorCallbackInfoPtr);

            if (PxBoxControllerDesc_isValid(_controllerDesc))
            {
                var shapes = stackalloc PxShape[1];
                _controller = PhysicsManager.instance.CreateController((PxControllerDesc*)_controllerDesc, this, &shapes);
                if (_controller != null)
                {
                    _shape = &shapes[0];
                    SetupFilterData(_shape);
                    RebuildFilterCallbacks();
                    PhysicsManager.instance.RegisterShape(_shape, this);
                    PhysicsManager.instance.RegisterCharacterController(this);
                }
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (actor != null)
            {
                PxActor_setActorFlag_mut((PxActor*)actor, PxActorFlag.DisableSimulation, false);
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (actor != null)
            {
                PxActor_setActorFlag_mut((PxActor*)actor, PxActorFlag.DisableSimulation, true);
            }
        }
        protected override void Update()
        {
            base.Update();
            if (_controller == null)
            {
                return;
            }
            if (gameObject.layer != _lastLayer)
            {
                RebuildFilterCallbacks();
            }
        }

        private void LateUpdate()
        {
            if (_controller == null)
            {
                return;
            }

            transform.position = position;
        }

        public PxControllerCollisionFlags Move(Vector3 displacement)
        {
            if (_controller == null)
            {
                return default;
            }
            if (!IsFinite(displacement))
            {
                Debug.LogWarning($"[BoxCharacterController] Ignoring non-finite displacement {displacement} on '{name}'.", this);
                return default;
            }
            var elapsedTime = _firstMove ? Time.fixedDeltaTime : Time.time - _lastMoveTime;
            elapsedTime = Mathf.Max(elapsedTime, Time.fixedDeltaTime);
            var positionBefore = position;
            if (!IsFinite(positionBefore))
            {
                Debug.LogWarning($"[BoxCharacterController] Controller '{name}' has non-finite position {positionBefore}; skipping Move.", this);
                return default;
            }
            var filterData = PxFilterData_new_2((uint)_cachedCollisionMask, (uint)GetInstanceID(), 0, 0);
            var filters = PxControllerFilters_new(&filterData, _queryFilterCallback, _controllerFilterCallback);
            filters.mFilterFlags = PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter;
            var flags = PxController_move_mut(_controller, (PxVec3*)&displacement, _minDist, elapsedTime, &filters, null);
            isGrounded = (flags & PxControllerCollisionFlags.CollisionDown) != 0;
            var positionAfter = position;
            _computedVelocity = IsFinite(positionAfter) ? (positionAfter - positionBefore) / elapsedTime : Vector3.zero;
            transform.position = positionAfter;
            _lastMoveTime = Time.time;
            _firstMove = false;
            return flags;
        }

        private static bool IsFinite(Vector3 v)
            => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
            && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        public void InvalidateCache()
        {
            if (_controller != null)
            {
                PxController_invalidateCache_mut(_controller);
            }
        }

        public override void Release()
        {
            if (_released)
            {
                return;
            }
            if (_controller != null)
            {
                PhysicsManager.instance.UnregisterCharacterController(this);
                var controllerActor = actor;
                if (controllerActor != null)
                {
                    PhysicsManager.instance.RemoveCollider((PxActor*)controllerActor);
                }
                if (_shape != null)
                {
                    PhysicsManager.instance.UnregisterShape(_shape);
                    _shape = null;
                }
                PxController_release_mut(_controller);
                _controller = null;
            }
            if (_controllerDesc != null)
            {
                if (_controllerDesc->reportCallback != null)
                {
                    destroy_user_controller_hit_report(_controllerDesc->reportCallback);
                }
                if (_controllerDesc->behaviorCallback != null)
                {
                    destroy_controller_behavior_callback(_controllerDesc->behaviorCallback);
                }
                PxBoxControllerDesc_delete(_controllerDesc);
                _controllerDesc = null;
            }
            if (_callbackInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_callbackInfoPtr);
                _callbackInfoPtr = IntPtr.Zero;
            }
            if (_behaviorCallbackInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_behaviorCallbackInfoPtr);
                _behaviorCallbackInfoPtr = IntPtr.Zero;
            }
            if (_queryFilterCallback != null)
            {
                PxQueryFilterCallback_delete(_queryFilterCallback);
                _queryFilterCallback = null;
            }
            if (_controllerFilterCallback != null)
            {
                destroy_controller_filter_callback(_controllerFilterCallback);
                _controllerFilterCallback = null;
            }
            _material = null;
            if (_physicsNamePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_physicsNamePtr);
                _physicsNamePtr = IntPtr.Zero;
            }
        }

        public override void RebuildFilterCallbacks()
        {
            _lastLayer = gameObject.layer;
            _cachedCollisionMask = PhysicsManager.instance.GetCollisionMask(gameObject.layer);
            if (_queryFilterCallback != null)
            {
                PxQueryFilterCallback_delete(_queryFilterCallback);
            }
            _queryFilterCallback = create_raycast_filter_callback_func(
                (delegate* unmanaged[Cdecl]<PxRigidActor*, PxFilterData*, PxShape*, uint, void*, PxQueryHitType>)_cctQueryPreFilterPtr,
                (void*)(IntPtr)_cachedCollisionMask);
            if (_controllerFilterCallback != null)
            {
                destroy_controller_filter_callback(_controllerFilterCallback);
            }
            _controllerFilterCallback = create_controller_filter_callback(
                (delegate* unmanaged[Cdecl]<PxController*, PxController*, void*, bool>)_cctControllerFilterPtr,
                (void*)(IntPtr)_cachedCollisionMask);
        }

        [MonoPInvokeCallback(typeof(CctQueryPreFilterDelegate))]
        private static PxQueryHitType CctQueryPreFilter(PxRigidActor* rigidActor, PxFilterData* filterData, PxShape* shape, uint hitFlags, void* userData)
        {
            var collisionMask = (uint)userData;
            var shapeFilterData = PxShape_getQueryFilterData(shape);
            if ((shapeFilterData.word0 & collisionMask) == 0)
            {
                return PxQueryHitType.None;
            }
            if (PhysicsManager.instance.GetIgnoreCollision(filterData->word1, shapeFilterData.word1))
            {
                return PxQueryHitType.None;
            }
            var shapeFlags = PxShape_getFlags(shape);
            var isTrigger = ((int)shapeFlags & (int)PxShapeFlag.TriggerShape) != 0;
            return isTrigger ? PxQueryHitType.Touch : PxQueryHitType.Block;
        }

        [MonoPInvokeCallback(typeof(CctControllerFilterDelegate))]
        private static bool CctControllerFilter(PxController* controllerA, PxController* controllerB, void* userData)
        {
            var actorA = (PxRigidActor*)PxController_getActor(controllerA);
            var flagsA = PxActor_getActorFlags((PxActor*)actorA);
            if ((flagsA & PxActorFlags.DisableSimulation) != 0)
            {
                return false;
            }
            var actorB = (PxRigidActor*)PxController_getActor(controllerB);
            var flagsB = PxActor_getActorFlags((PxActor*)actorB);
            if ((flagsB & PxActorFlags.DisableSimulation) != 0)
            {
                return false;
            }
            var collisionMask = (uint)userData;
            var shapesB = stackalloc PxShape[1];
            PxRigidActor_getShapes(actorB, &shapesB, 1, 0);
            var shapeFilterData = PxShape_getQueryFilterData(&shapesB[0]);
            if ((shapeFilterData.word0 & collisionMask) == 0)
            {
                return false;
            }

            var colliderA = PhysicsManager.instance.GetCollider((PxActor*)actorA);
            var colliderB = PhysicsManager.instance.GetCollider((PxActor*)actorB);
            if (colliderA == null || colliderB == null)
            {
                return true;
            }

            return !PhysicsManager.instance.GetIgnoreCollision(colliderA, colliderB);
        }

        [MonoPInvokeCallback(typeof(OnControllerHitDelegate))]
        private static void OnControllerHit(PxControllersHit* hit) { }

        [MonoPInvokeCallback(typeof(OnDestructorDelegate))]
        private static void OnDestructor() { }

        [MonoPInvokeCallback(typeof(OnObstacleHitDelegate))]
        private static void OnObstacleHit(PxControllerObstacleHit* hit) { }

        [MonoPInvokeCallback(typeof(OnShapeHitDelegate))]
        private static void OnShapeHit(PxControllerShapeHit* hit)
        {
            var hitActor = hit->actor;
            if (hitActor == null)
            {
                return;
            }
            var hitCollider = PhysicsManager.instance.GetCollider((PxActor*)hitActor, hit->shape);
            if (hitCollider == null)
            {
                return;
            }
            if (hit->controller == null)
            {
                return;
            }
            var controllerActor = PxController_getActor(hit->controller);
            if (controllerActor == null)
            {
                return;
            }
            var controllerCollider = PhysicsManager.instance.GetCollider((PxActor*)controllerActor);
            if (controllerCollider == null)
            {
                return;
            }
            _cachedControllerShapeHit.collider = hitCollider;
            _cachedControllerShapeHit.dir = hit->dir;
            _cachedControllerShapeHit.length = hit->length;
            _cachedControllerShapeHit.triangleIndex = (int)hit->triangleIndex;
            _cachedControllerShapeHit.worldNormal = hit->worldNormal;
            _cachedControllerShapeHit.worldPos.x = (float)hit->worldPos.x;
            _cachedControllerShapeHit.worldPos.y = (float)hit->worldPos.y;
            _cachedControllerShapeHit.worldPos.z = (float)hit->worldPos.z;
            if (OnPhysXControllerColliderHit != null)
            {
                OnPhysXControllerColliderHit((BoxCharacterController)controllerCollider, _cachedControllerShapeHit);
                return;
            }
            controllerCollider.gameObject.SendMessage("OnPhysXControllerColliderHit", _cachedControllerShapeHit, SendMessageOptions.DontRequireReceiver);
        }

        [MonoPInvokeCallback(typeof(GetBehaviorFlagsShapeDelegate))]
        private static PxControllerBehaviorFlags GetBehaviorFlagsShape(PxController* sourceController, PxShape* shape, PxActor* actor)
            => PxControllerBehaviorFlags.CctCanRideOnObject;

        [MonoPInvokeCallback(typeof(GetBehaviorFlagsControllerDelegate))]
        private static PxControllerBehaviorFlags GetBehaviorFlagsController(PxController* sourceController, PxController* otherController)
            => PxControllerBehaviorFlags.CctSlide;

        [MonoPInvokeCallback(typeof(GetBehaviorFlagsObstacleDelegate))]
        private static PxControllerBehaviorFlags GetBehaviorFlagsObstacle(PxController* sourceController, PxObstacle* obstacle)
            => PxControllerBehaviorFlags.CctCanRideOnObject;
    }
}
