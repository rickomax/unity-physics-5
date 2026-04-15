using AOT;
using MagicPhysX;
using System;
using System.Runtime.InteropServices;
using UnityEngine;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
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

        [SerializeField] private float _halfForwardExtent = 0.5f;
        [SerializeField] private float _halfHeight = 0.5f;
        [SerializeField] private float _halfSideExtent = 0.5f;
        [SerializeField] private float _contactOffset = 0.1f;
        [SerializeField] private float _slopeLimit = 45f;
        [SerializeField] private float _stepOffset = 0.5f;
        [SerializeField] private float _invisibleWallHeight;
        [SerializeField] private float _maxJumpHeight;
        [SerializeField] private float _minDist = 0.001f;
        [SerializeField] private float _scaleCoeff = 0.8f;
        [SerializeField] private float _volumeGrowth = 1.5f;
        [SerializeField] private Vector3 _upDirection = Vector3.up;
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
        private Vector3 _lastScale;
        private int _lastLayer;
        private int _cachedCollisionMask;

        protected override bool supportsAttachedRigidbody => false;

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
            get => _halfForwardExtent;
            set
            {
                _halfForwardExtent = value;
                ApplyScaledExtentsToController();
            }
        }

        public float halfHeight
        {
            get => _halfHeight;
            set
            {
                _halfHeight = value;
                ApplyScaledExtentsToController();
            }
        }

        public float halfSideExtent
        {
            get => _halfSideExtent;
            set
            {
                _halfSideExtent = value;
                ApplyScaledExtentsToController();
            }
        }

        public float contactOffset
        {
            get => _contactOffset;
            set
            {
                _contactOffset = value;
                if (_controller != null)
                {
                    PxController_setContactOffset_mut(_controller, value);
                }
            }
        }

        public float slopeLimit
        {
            get => _slopeLimit;
            set
            {
                _slopeLimit = value;
                if (_controller != null)
                {
                    PxController_setSlopeLimit_mut(_controller, value);
                }
            }
        }

        public float stepOffset
        {
            get => _stepOffset;
            set
            {
                _stepOffset = value;
                if (_controller != null)
                {
                    PxController_setStepOffset_mut(_controller, value);
                }
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
                    throw new NullReferenceException();
                }
                var foot = PxExtendedVec3_new_1(value.x, value.y, value.z);
                PxController_setFootPosition_mut(_controller, &foot);
            }
        }

        public Vector3 position
        {
            get
            {
                if (_controller == null)
                {
                    return transform.position;
                }
                var pos = PxController_getPosition(_controller);
                return new Vector3((float)pos->x, (float)pos->y, (float)pos->z);
            }
            set
            {
                if (_controller == null)
                {
                    throw new NullReferenceException();
                }
                var pos = PxExtendedVec3_new_1(value.x, value.y, value.z);
                PxController_setPosition_mut(_controller, &pos);
            }
        }

        public Quaternion rotation
        {
            get => transform.rotation;
            set => transform.rotation = value;
        }

        public Vector3 velocity => _computedVelocity;

        protected override void Awake()
        {
            base.Awake();

            var pxPosition = (PxVec3)transform.position;

            _controllerDesc = PxBoxControllerDesc_new_alloc();
            PxBoxControllerDesc_setToDefault_mut(_controllerDesc);
            _controllerDesc->position.x = pxPosition.x;
            _controllerDesc->position.y = pxPosition.y;
            _controllerDesc->position.z = pxPosition.z;
            _controllerDesc->upDirection = _upDirection;
            _controllerDesc->slopeLimit = _slopeLimit * Mathf.Deg2Rad;
            _controllerDesc->invisibleWallHeight = _invisibleWallHeight;
            _controllerDesc->maxJumpHeight = _maxJumpHeight;
            _controllerDesc->contactOffset = _contactOffset;
            _controllerDesc->stepOffset = _stepOffset;
            _controllerDesc->density = density;
            _controllerDesc->scaleCoeff = _scaleCoeff;
            _controllerDesc->volumeGrowth = _volumeGrowth;
            _controllerDesc->nonWalkableMode = _nonWalkableMode;
            _controllerDesc->material = _material;
            ApplyScaledExtentsToControllerDesc();

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
                    PxShape_setFlag_mut(_shape, PxShapeFlag.SceneQueryShape, true);
                    SetupFilterData(_shape);
                    _lastScale = GetPhysicsScale();
                    RebuildFilterCallbacks();
                    PhysicsManager.instance.RegisterShape(_shape, this);
                    PhysicsManager.instance.RegisterCharacterController(this);
                }
            }
        }

        protected override void Update()
        {
            base.Update();
            if (_controller == null)
            {
                return;
            }
            var scale = GetPhysicsScale();
            if (scale != _lastScale)
            {
                ApplyScaledExtentsToController();
                _lastScale = scale;
            }
            if (gameObject.layer != _lastLayer)
            {
                RebuildFilterCallbacks();
            }
        }

        public PxControllerCollisionFlags Move(Vector3 displacement)
        {
            if (_controller == null)
            {
                throw new NullReferenceException();
            }
            var elapsedTime = _firstMove ? Time.fixedDeltaTime : Time.time - _lastMoveTime;
            elapsedTime = Mathf.Max(elapsedTime, Time.fixedDeltaTime);
            _firstMove = false;
            var positionBefore = position;
            var filterData = PxFilterData_new_2((uint)_cachedCollisionMask, 0, 0, 0);
            var filters = PxControllerFilters_new(&filterData, _queryFilterCallback, _controllerFilterCallback);
            filters.mFilterFlags = PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter;
            var flags = PxController_move_mut(_controller, (PxVec3*)&displacement, _minDist, elapsedTime, &filters, null);
            isGrounded = (flags & PxControllerCollisionFlags.CollisionDown) != 0;
            _computedVelocity = (position - positionBefore) / elapsedTime;
            _lastMoveTime = Time.time;
            transform.position = position;
            return flags;
        }

        public void InvalidateCache()
        {
            if (_controller != null)
            {
                PxController_invalidateCache_mut(_controller);
            }
        }

        public void SyncTransformFromPhysics()
        {
            transform.position = position;
        }

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            transform.position = position;
            transform.rotation = rotation;
        }

        public override void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            if (_controller != null)
            {
                PhysicsManager.instance.UnregisterCharacterController(this);
                var controllerActor = (PxRigidActor*)PxController_getActor(_controller);
                if (controllerActor != null && PhysicsManager.instance != null)
                {
                    PhysicsManager.instance.RemoveCollider((PxActor*)controllerActor);
                }

                if (_shape != null)
                {
                    PhysicsManager.instance.UnregisterShape(_shape);
                    PxController_release_mut(_controller);
                    _controller = null;
                }
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

            if (_material != null)
            {
                //todo: release material?
                _material = null;
            }
        }

        private Vector3 GetScaledHalfExtents()
        {
            return Vector3.Scale(new Vector3(_halfSideExtent, _halfHeight, _halfForwardExtent), GetPhysicsScale());
        }

        private void ApplyScaledExtentsToControllerDesc()
        {
            if (_controllerDesc == null)
            {
                return;
            }
            var scaledHalfExtents = GetScaledHalfExtents();
            _controllerDesc->halfSideExtent = scaledHalfExtents.x;
            _controllerDesc->halfHeight = scaledHalfExtents.y;
            _controllerDesc->halfForwardExtent = scaledHalfExtents.z;
        }

        private void ApplyScaledExtentsToController()
        {
            ApplyScaledExtentsToControllerDesc();
            if (_controller == null)
            {
                return;
            }
            var scaledHalfExtents = GetScaledHalfExtents();
            PxBoxController_setHalfSideExtent_mut((PxBoxController*)_controller, scaledHalfExtents.x);
            PxBoxController_setHalfHeight_mut((PxBoxController*)_controller, scaledHalfExtents.y);
            PxBoxController_setHalfForwardExtent_mut((PxBoxController*)_controller, scaledHalfExtents.z);
        }

        private void RebuildFilterCallbacks()
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
            var collisionMask = (uint)(IntPtr)userData;
            var shapeFilterData = PxShape_getQueryFilterData(shape);
            return (shapeFilterData.word0 & collisionMask) != 0 ? PxQueryHitType.Block : PxQueryHitType.None;
        }

        [MonoPInvokeCallback(typeof(CctControllerFilterDelegate))]
        private static bool CctControllerFilter(PxController* controllerA, PxController* controllerB, void* userData)
        {
            var collisionMask = (uint)(IntPtr)userData;
            var actorB = (PxRigidActor*)PxController_getActor(controllerB);
            if (actorB == null)
            {
                return true;
            }
            var shapes = stackalloc PxShape[1];
            PxRigidActor_getShapes(actorB, &shapes, 1, 0);
            var shapeFilterData = PxShape_getQueryFilterData(&shapes[0]);
            return (shapeFilterData.word0 & collisionMask) != 0;
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
            controllerCollider.gameObject.SendMessage("OnControllerColliderHit", _cachedControllerShapeHit, SendMessageOptions.DontRequireReceiver);
        }

        //todo: expose this as a Func for customization
        [MonoPInvokeCallback(typeof(GetBehaviorFlagsShapeDelegate))]
        private static PxControllerBehaviorFlags GetBehaviorFlagsShape(PxController* sourceController, PxShape* shape, PxActor* actor)
        {
            return PxControllerBehaviorFlags.CctCanRideOnObject;
        }

        //todo: expose this as a Func for customization
        [MonoPInvokeCallback(typeof(GetBehaviorFlagsControllerDelegate))]
        private static PxControllerBehaviorFlags GetBehaviorFlagsController(PxController* sourceController, PxController* otherController)
        {
            return PxControllerBehaviorFlags.CctSlide;
        }

        //todo: expose this as a Func for customization
        [MonoPInvokeCallback(typeof(GetBehaviorFlagsObstacleDelegate))]
        private static PxControllerBehaviorFlags GetBehaviorFlagsObstacle(PxController* sourceController, PxObstacle* obstacle)
        {
            return PxControllerBehaviorFlags.CctCanRideOnObject;
        }
    }
}
