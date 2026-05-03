using AOT;
using MagicPhysX;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    [DefaultExecutionOrder(-10)]
    public unsafe class PhysicsManager : MonoBehaviour
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ContactDelegate(void* userData, PxContactPairHeader* pairHeader, PxContactPair* contactPair, uint nbPairs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate PxFilterFlags CustomFilterShaderDelegate(FilterShaderCallbackInfo* callbackInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate PxQueryHitType PreFilterDelegate(PxRigidActor* rigidActor, PxFilterData* filterData, PxShape* shape, uint hitFlags, void* userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate PxQueryHitType PostFilterDelegate(PxFilterData* filterData, PxQueryHit* hit, void* userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ReportErrorDelegate(PxErrorCode code, sbyte* message, sbyte* file, uint line, void* userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void TriggerDelegate(void* userData, PxTriggerPair* pairs, uint count);

        public delegate void TriggerEventDelegate(Collider a, Collider b);
        public delegate void CollisionEventDelegate(Collider a, Collider b);

        public static TriggerEventDelegate OnPhysXTriggerEnter;
        public static TriggerEventDelegate OnPhysXTriggerStay;
        public static TriggerEventDelegate OnPhysXTriggerExit;
        public static CollisionEventDelegate OnPhysXCollisionEnter;
        public static CollisionEventDelegate OnPhysXCollisionStay;
        public static CollisionEventDelegate OnPhysXCollisionExit;

        public struct LayerPairKey
        {
            public int layerA;
            public int layerB;

            public LayerPairKey(int layerA, int layerB) { this.layerA = layerA; this.layerB = layerB; }

            public override bool Equals(object obj) => obj is LayerPairKey other && ((layerA == other.layerA && layerB == other.layerB) || (layerA == other.layerB && layerB == other.layerA));

            public override int GetHashCode() => layerA < layerB ? (layerA * 31 + layerB) : (layerB * 31 + layerA);
        }

        public struct ColliderPairKey
        {
            public uint colliderA;
            public uint colliderB;

            public ColliderPairKey(uint colliderA, uint colliderB)
            {
                this.colliderA = colliderA;
                this.colliderB = colliderB;
            }

            public override bool Equals(object obj) => obj is ColliderPairKey other && ((colliderA == other.colliderA && colliderB == other.colliderB) || (colliderA == other.colliderB && colliderB == other.colliderA));

            public override int GetHashCode() => colliderA < colliderB ? ((int)colliderA * 31 + (int)colliderB) : ((int)colliderB * 31 + (int)colliderA);
        }

        private const float InitialOverlapEpsilon = 1e-4f;
        private const uint PhysXVersionMajor = 5u;
        private const uint PhysXVersionMinor = 1u;
        private const uint PhysXVersionBugfix = 3u;
        private const uint PhysXVersionNumber = (PhysXVersionMajor << 24) | (PhysXVersionMinor << 16) | (PhysXVersionBugfix << 8);

        private static readonly ReportErrorDelegate _reportError = ReportError;
        private static readonly CustomFilterShaderDelegate _customFilterShader = CustomFilterShader;
        private static readonly ContactDelegate _contact = Contact;
        private static readonly PreFilterDelegate _blockingPreFilter = BlockingPreFilter;
        private static readonly PreFilterDelegate _nonBlockingPreFilter = NonBlockingPreFilter;
        private static readonly PostFilterDelegate _blockingPostFilter = BlockingPostFilter;
        private static readonly TriggerDelegate _trigger = Trigger;
        private static readonly IntPtr _blockingPreFilterPtr = Marshal.GetFunctionPointerForDelegate(_blockingPreFilter);
        private static readonly IntPtr _nonBlockingPreFilterPtr = Marshal.GetFunctionPointerForDelegate(_nonBlockingPreFilter);
        private static readonly IntPtr _blockingPostFilterPtr = Marshal.GetFunctionPointerForDelegate(_blockingPostFilter);

        public uint scratchBlockSize = 16 * 1024;
        public int maxHits = 2048;
        public int layerCount = 32;
        public float toleranceLength = 1f;
        public float toleranceSpeed = 10f;
        public string pvdAddress = "127.0.0.1";
        public int pvdPort = 5425;
        public bool connectToPVD;

        [SerializeField] private float _updateInterval = 0f;

        private readonly Dictionary<IntPtr, Collider> _colliders = new Dictionary<IntPtr, Collider>();
        private readonly Dictionary<IntPtr, Collider> _shapeColliders = new Dictionary<IntPtr, Collider>();
        private readonly Dictionary<Mesh, IntPtr> _triangleMeshes = new Dictionary<Mesh, IntPtr>();
        private readonly Dictionary<Mesh, IntPtr> _convexMeshes = new Dictionary<Mesh, IntPtr>();
        private readonly HashSet<BoxCharacterController> _characterControllers = new HashSet<BoxCharacterController>();

        private Rigidbody _dummyRigidbody;
        private PxTolerancesScale _toleranceScale;
        private PxPhysics* _physics;
        private PxFoundation* _foundation;
        private PxPvd* _pvd;
        private PxScene* _scene;
        private PxSceneDesc* _sceneDesc;
        private IntPtr _sceneDescPtr;
        private PxDefaultCpuDispatcher* _dispatcher;
        private PxErrorCallback* _errorCallback;
        private PxControllerManager* _controllerManager;
        private PxSimulationEventCallback* _simulationEventCallback;
        private PxQueryFilterCallback* _blockingQueryFilterCallback;
        private PxQueryFilterCallback* _nonBlockingQueryFilterCallback;
        private byte* _scratchBuffer;
        private HashSet<LayerPairKey> _ignoredLayerCollisions;
        private HashSet<ColliderPairKey> _ignoredColliderCollisions;
        private Dictionary<int, string> _layerNames;
        private int[] _collisionMasks;
        private float _stepAccumulator;
        private int _nextId = 1;

        private BoxCollider _sweepBoxCollider;
        private SphereCollider _sweepSphereCollider;

        public static PhysicsManager instance { get; private set; }
        public static bool isShuttingDown { get; private set; }
        public Rigidbody dummyRigidbody => _dummyRigidbody;
        public float updateInterval => _updateInterval == 0f ? Time.fixedDeltaTime : _updateInterval;
        public PxPhysics* physics => _physics;

        public Vector3 gravity
        {
            get
            {
                if (_scene == null)
                {
                    return default;
                }
                return PxScene_getGravity(_scene);
            }
            set
            {
                if (_scene == null)
                {
                    return;
                }
                var pxVec3 = (PxVec3)value;
                PxScene_setGravity_mut(_scene, &pxVec3);
            }
        }

        private void Awake()
        {
            instance = this;
            InitialSetup();
            CreatePhysics();
            CreateScene();
            CreateCallbacks();
            CreateControllerManager();
            CreateDummyRigidbody();
            CreateSweepColliders();
            //var pvdClient = PxScene_getScenePvdClient_mut(_scene);
            //if (pvdClient != null)
            //{
            //    PxPvdSceneClient_setScenePvdFlag_mut(pvdClient, PxPvdSceneFlag.TransmitScenequeries, true);
            //}
        }

        private void FixedUpdate()
        {
            if (_scene == null)
            {
                return;
            }
            var step = updateInterval;
            if (step <= 0f)
            {
                return;
            }
            _stepAccumulator += Time.fixedDeltaTime;
            while (_stepAccumulator >= step)
            {
                UpdateInternal(step);
                _stepAccumulator -= step;
            }
        }

        private void CreateSweepColliders()
        {   
            var sweepGameObject = new GameObject("SweepColliders");
            sweepGameObject.transform.SetParent(transform, false);
            _sweepBoxCollider = sweepGameObject.AddComponent<BoxCollider>();
            _sweepBoxCollider.halfExtents = new Vector3(0.5f, 0.5f, 0.5f);
            _sweepBoxCollider.enabled = false;
            _sweepSphereCollider = sweepGameObject.AddComponent<SphereCollider>();
            _sweepSphereCollider.radius = 0.5f;
            _sweepSphereCollider.enabled = false;
        }

        public void OnDestroy()
        {
            instance = null;
            isShuttingDown = true;
            OnPhysXTriggerEnter = null;
            OnPhysXTriggerStay = null;
            OnPhysXTriggerExit = null;
            OnPhysXCollisionEnter = null;
            OnPhysXCollisionStay = null;
            OnPhysXCollisionExit = null;
            if (_colliders.Count > 0)
            {
                var collidersToRelease = new HashSet<Collider>(_colliders.Values);
                foreach (var collider in collidersToRelease)
                {
                    collider?.Release();
                }
            }
            _dummyRigidbody = null;
            _colliders.Clear();
            _shapeColliders.Clear();
            _characterControllers.Clear();
            _ignoredColliderCollisions.Clear();
            foreach (var entry in _triangleMeshes)
            {
                PxTriangleMesh_release_mut((PxTriangleMesh*)entry.Value);
            }
            _triangleMeshes.Clear();
            foreach (var entry in _convexMeshes)
            {
                PxConvexMesh_release_mut((PxConvexMesh*)entry.Value);
            }
            _convexMeshes.Clear();
            if (_controllerManager != null)
            {
                PxControllerManager_release_mut(_controllerManager);
                _controllerManager = null;
            }
            if (_scene != null)
            {
                PxScene_setSimulationEventCallback_mut(_scene, null);
            }
            if (_simulationEventCallback != null)
            {
                PxSimulationEventCallback_delete(_simulationEventCallback);
                _simulationEventCallback = null;
            }
            if (_blockingQueryFilterCallback != null)
            {
                PxQueryFilterCallback_delete(_blockingQueryFilterCallback);
                _blockingQueryFilterCallback = null;
            }
            if (_nonBlockingQueryFilterCallback != null)
            {
                PxQueryFilterCallback_delete(_nonBlockingQueryFilterCallback);
                _nonBlockingQueryFilterCallback = null;
            }
            if (_scene != null)
            {
                PxScene_release_mut(_scene);
                _scene = null;
            }
            if (_dispatcher != null)
            {
                PxDefaultCpuDispatcher_release_mut(_dispatcher);
                _dispatcher = null;
            }
            if (connectToPVD && _pvd != null)
            {
                phys_PxCloseExtensions();
            }
            if (_physics != null)
            {
                PxPhysics_release_mut(_physics);
                _physics = null;
            }
            if (connectToPVD && _pvd != null)
            {
                PxPvd_disconnect_mut(_pvd);
                PxPvd_release_mut(_pvd);
                _pvd = null;
            }
            if (_sceneDescPtr != IntPtr.Zero)
            {
                free_custom_filter_shader(_sceneDesc);
                Marshal.FreeHGlobal(_sceneDescPtr);
                _sceneDescPtr = IntPtr.Zero;
            }
            if (_foundation != null)
            {
                if (_errorCallback != null)
                {
                    PxFoundation_deregisterErrorCallback_mut(_foundation, _errorCallback);
                }
                PxFoundation_release_mut(_foundation);
                _foundation = null;
            }
            if (_errorCallback != null)
            {
                PxErrorCallback_delete(_errorCallback);
                _errorCallback = null;
            }
            if (_scratchBuffer != null)
            {
                UnsafeUtility.Free(_scratchBuffer, Allocator.Persistent);
                _scratchBuffer = null;
            }
        }

        private void InitialSetup()
        {
            isShuttingDown = false;
            Physics.simulationMode = SimulationMode.Script;
            BuildLayerCollisionDictionary();
            _scratchBuffer = (byte*)UnsafeUtility.Malloc(scratchBlockSize, 16, Allocator.Persistent);
            _toleranceScale = PxTolerancesScale_new(toleranceLength, toleranceSpeed);
            _foundation = physx_create_foundation();
            _errorCallback = create_error_callback((delegate* unmanaged[Cdecl]<PxErrorCode, sbyte*, sbyte*, uint, void*, void>)Marshal.GetFunctionPointerForDelegate(_reportError), null);
            PxFoundation_registerErrorCallback_mut(_foundation, _errorCallback);
        }

        private void CreatePhysics()
        {
            if (connectToPVD)
            {
                var uriBytes = Encoding.ASCII.GetBytes(pvdAddress + "\0");
                fixed (byte* bytePointer = uriBytes)
                {
                    var transport = phys_PxDefaultPvdSocketTransportCreate(bytePointer, pvdPort, 10000);
                    if (transport == null)
                    {
                        throw new Exception("Unable to create PVD transport");
                    }
                    _pvd = phys_PxCreatePvd(_foundation);
                    PxPvd_connect_mut(_pvd, transport, PxPvdInstrumentationFlags.All);
                }
                _physics = phys_PxCreatePhysics(PhysXVersionNumber, _foundation, (PxTolerancesScale*)Unsafe.AsPointer(ref _toleranceScale), true, _pvd, null);
                if (_physics == null)
                {
                    throw new Exception("Unable to create PhysX system");
                }
                phys_PxInitExtensions(_physics, _pvd);
            }
            else
            {
                _physics = physx_create_physics(_foundation);
            }
            if (_physics == null)
            {
                throw new Exception("Unable to create PhysX system");
            }
        }

        private void CreateScene()
        {
            _sceneDescPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PxSceneDesc>());
            _sceneDesc = (PxSceneDesc*)_sceneDescPtr;
            PxSceneDesc_setToDefault_mut(_sceneDesc, (PxTolerancesScale*)Unsafe.AsPointer(ref _toleranceScale));
            _sceneDesc->gravity = Physics.gravity;
            var workerCount = (uint)Math.Clamp(SystemInfo.processorCount - 1, 2, 4);
            _dispatcher = phys_PxDefaultCpuDispatcherCreate(workerCount, null, PxDefaultCpuDispatcherWaitForWorkMode.WaitForWork, 0);
            _sceneDesc->cpuDispatcher = (PxCpuDispatcher*)_dispatcher;
            _sceneDesc->kineKineFilteringMode = PxPairFilteringMode.Keep;
            _sceneDesc->staticKineFilteringMode = PxPairFilteringMode.Keep;
            _sceneDesc->broadPhaseType = PxBroadPhaseType.Sap;
            _sceneDesc->flags |= PxSceneFlags.EnablePcm | PxSceneFlags.EnableCcd | PxSceneFlags.EnableStabilization;
            enable_custom_filter_shader(_sceneDesc, (delegate* unmanaged[Cdecl]<FilterShaderCallbackInfo*, PxFilterFlags>)Marshal.GetFunctionPointerForDelegate(_customFilterShader), 0u);
            if (!PxSceneDesc_isValid(_sceneDesc))
            {
                throw new Exception("Invalid PhysX scene description");
            }
            _scene = PxPhysics_createScene_mut(_physics, _sceneDesc);
            if (_scene == null)
            {
                throw new Exception("Could not create PhysX scene");
            }
        }

        private void CreateCallbacks()
        {
            var simulationEventCallbackInfo = new SimulationEventCallbackInfo();
            simulationEventCallbackInfo.collision_callback = (delegate* unmanaged[Cdecl]<void*, PxContactPairHeader*, PxContactPair*, uint, void>)Marshal.GetFunctionPointerForDelegate(_contact);
            simulationEventCallbackInfo.trigger_callback = (delegate* unmanaged[Cdecl]<void*, PxTriggerPair*, uint, void>)Marshal.GetFunctionPointerForDelegate(_trigger);
            _simulationEventCallback = create_simulation_event_callbacks(&simulationEventCallbackInfo);
            if (_simulationEventCallback == null)
            {
                throw new Exception("Error creating PhysX collision callback");
            }
            PxScene_setSimulationEventCallback_mut(_scene, _simulationEventCallback);
            _blockingQueryFilterCallback = create_pre_and_post_raycast_filter_callback_func((delegate* unmanaged[Cdecl]<PxRigidActor*, PxFilterData*, PxShape*, uint, void*, PxQueryHitType>)_blockingPreFilterPtr, (delegate* unmanaged[Cdecl]<PxFilterData*, PxQueryHit*, void*, PxQueryHitType>)_blockingPostFilterPtr, null);
            _nonBlockingQueryFilterCallback = create_raycast_filter_callback_func((delegate* unmanaged[Cdecl]<PxRigidActor*, PxFilterData*, PxShape*, uint, void*, PxQueryHitType>)_nonBlockingPreFilterPtr, null);
        }

        private void CreateControllerManager()
        {
            _controllerManager = phys_PxCreateControllerManager(_scene, false);
            //PxControllerManager_setOverlapRecoveryModule_mut(_controllerManager, false);
            //PxControllerManager_setPreciseSweeps_mut(_controllerManager, false);
        }

        private void CreateDummyRigidbody()
        {
            _dummyRigidbody = GetComponent<Rigidbody>();
            if (_dummyRigidbody == null)
            {
                _dummyRigidbody = gameObject.AddComponent<Rigidbody>();
            }
            _dummyRigidbody.isPhysicsStatic = true;
            _dummyRigidbody.isKinematic = false;
            _dummyRigidbody.useGravity = false;
        }

        private void BuildLayerCollisionDictionary()
        {
            _ignoredColliderCollisions = new HashSet<ColliderPairKey>();
            _ignoredLayerCollisions = new HashSet<LayerPairKey>();
            _collisionMasks = new int[layerCount];
            for (var layer1 = 0; layer1 < layerCount; layer1++)
            {
                for (var layer2 = 0; layer2 < layerCount; layer2++)
                {
                    if (Physics.GetIgnoreLayerCollision(layer1, layer2))
                    {
                        _ignoredLayerCollisions.Add(new LayerPairKey(layer1, layer2));
                    }
                    else
                    {
                        _collisionMasks[layer1] |= 1 << layer2;
                    }
                }
            }
            _layerNames = new Dictionary<int, string>();
            for (var layer = 0; layer < layerCount; layer++)
            {
                _layerNames[layer] = LayerMask.LayerToName(layer);
            }
        }

        private void UpdateInternal(float dt)
        {
            if (_scene == null)
            {
                return;
            }
            PxScene_simulate_mut(_scene, dt, null, _scratchBuffer, scratchBlockSize, true);
            uint error = 0;
            PxScene_fetchResults_mut(_scene, true, &error);
            if (error != 0)
            {
                Debug.LogError($"PhysX Error: {(PxErrorCode)error}");
            }
        }

        public bool AddActor(PxActor* pxActor, Collider collider)
        {
            if (PxScene_addActor_mut(_scene, pxActor, null))
            {
                var id = _nextId++;
                pxActor->userData = (void*)id;
                _colliders[(IntPtr)id] = collider;
                return true;
            }
            return false;
        }

        public void RemoveCollider(PxActor* pxActor)
        {
            if (pxActor == null)
            {
                return;
            }
            _colliders.Remove((IntPtr)pxActor->userData);
        }

        public Collider GetCollider(PxActor* pxActor)
        {
            return pxActor == null ? null : _colliders.GetValueOrDefault((IntPtr)pxActor->userData);
        }

        public Collider GetCollider(PxActor* pxActor, PxShape* pxShape)
        {
            if (pxShape != null && _shapeColliders.TryGetValue((IntPtr)pxShape, out var collider))
            {
                return collider;
            }
            return GetCollider(pxActor);
        }

        public void RegisterShape(PxShape* pxShape, Collider collider)
        {
            if (pxShape == null || collider == null)
            {
                return;
            }
            _shapeColliders[(IntPtr)pxShape] = collider;
        }

        public void UnregisterShape(PxShape* pxShape)
        {
            if (pxShape == null)
            {
                return;
            }
            _shapeColliders.Remove((IntPtr)pxShape);
        }

        public void RegisterCharacterController(BoxCharacterController controller)
        {
            if (controller != null)
            {
                _characterControllers.Add(controller);
            }
        }

        public void UnregisterCharacterController(BoxCharacterController controller)
        {
            if (controller != null)
            {
                _characterControllers.Remove(controller);
            }
        }

        public PxController* CreateController(PxControllerDesc* controllerDesc, Collider collider, PxShape** shapes)
        {
            if (!PxControllerDesc_isValid(controllerDesc))
            {
                return null;
            }
            var controller = PxControllerManager_createController_mut(_controllerManager, controllerDesc);
            if (controller == null)
            {
                return null;
            }
            var controllerActor = (PxRigidActor*)PxController_getActor(controller);
            var id = _nextId++;
            controllerActor->userData = (void*)id;
            _colliders.Add((IntPtr)id, collider);
            PxRigidActor_getShapes(controllerActor, shapes, 1, 0);
            return controller;
        }

        public bool ComputePenetration(out Vector3 direction, out float depth, Collider colliderA, Collider colliderB, Vector3 positionA, Quaternion rotationA, Vector3 positionB, Quaternion rotationB)
        {
            var shapeA = colliderA.shape;
            var shapeB = colliderB.shape;
            if (shapeA == null || shapeB == null)
            {
                throw new NullReferenceException();
            }
            direction = default;
            depth = default;
            var geometryA = PxShape_getGeometry(shapeA);
            var geometryB = PxShape_getGeometry(shapeB);
            var poseA = colliderA.GetQueryPose(positionA, rotationA);
            var poseB = colliderB.GetQueryPose(positionB, rotationB);
            return PxGeometryQuery_computePenetration((PxVec3*)Unsafe.AsPointer(ref direction), (float*)Unsafe.AsPointer(ref depth), geometryA, &poseA, geometryB, &poseB, PxGeometryQueryFlags.SimdGuard);
        }

        public bool Linecast(Vector3 origin, Vector3 target, out RaycastHit raycastHit, int mask, bool hitTriggers = false)
        {
            var toTarget = target - origin;
            return Raycast(origin, toTarget.normalized, toTarget.magnitude, out raycastHit, mask, hitTriggers);
        }

        public int LinecastAll(Vector3 origin, Vector3 target, RaycastHit[] raycastHits, int mask, out bool outBlockingHit, bool hitTriggers = false)
        {
            var toTarget = target - origin;
            return RaycastAll(origin, toTarget.normalized, toTarget.magnitude, raycastHits, mask, out outBlockingHit, hitTriggers);
        }

        public bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit raycastHit, int mask, bool hitTriggers = false)
        {
            if (float.IsInfinity(distance))
            {
                distance = float.MaxValue;
            }
            direction.Normalize();
            var outputFlags = PxHitFlags.Default | PxHitFlags.Position | PxHitFlags.Normal | PxHitFlags.Uv;
            var filterData = PxQueryFilterData_new();
            filterData.flags |= PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter | PxQueryFlags.Postfilter;
            filterData.data.word0 = (uint)mask;
            PxRaycastHit pxRaycastHit = default;
            var result = PxSceneQueryExt_raycastSingle(_scene, (PxVec3*)&origin, (PxVec3*)&direction, distance, outputFlags, &pxRaycastHit, &filterData, _blockingQueryFilterCallback, null);
            raycastHit.distance = pxRaycastHit.distance;
            raycastHit.position = pxRaycastHit.position;
            raycastHit.collider = GetCollider((PxActor*)pxRaycastHit.actor, pxRaycastHit.shape);
            raycastHit.faceIndex = (int)pxRaycastHit.faceIndex;
            raycastHit.flags = pxRaycastHit.flags;
            raycastHit.normal = pxRaycastHit.normal;
            raycastHit.u = pxRaycastHit.u;
            raycastHit.v = pxRaycastHit.v;
            return result;
        }

        public int RaycastAll(Vector3 origin, Vector3 direction, float distance, RaycastHit[] raycastHits, int mask, out bool outBlockingHit, bool hitTriggers = false)
        {
            if (raycastHits == null)
            {
                throw new ArgumentNullException(nameof(raycastHits));
            }
            if (float.IsInfinity(distance))
            {
                distance = float.MaxValue;
            }
            direction.Normalize();
            var outputFlags = PxHitFlags.Default | PxHitFlags.Position | PxHitFlags.Normal | PxHitFlags.Uv;
            var filterData = PxQueryFilterData_new();
            filterData.flags |= PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter;
            filterData.data.word0 = (uint)mask;
            var blockingHit = false;
            var hitCapacity = Math.Min(raycastHits.Length, maxHits);
            var pxRaycastHits = stackalloc PxRaycastHit[maxHits];
            var result = PxSceneQueryExt_raycastMultiple(_scene, (PxVec3*)&origin, (PxVec3*)&direction, distance, outputFlags, pxRaycastHits, (uint)hitCapacity, &blockingHit, &filterData, _nonBlockingQueryFilterCallback, null);
            outBlockingHit = blockingHit;
            for (var i = 0; i < result; i++)
            {
                var pxRaycastHit = pxRaycastHits[i];
                RaycastHit raycastHit = default;
                raycastHit.distance = pxRaycastHit.distance;
                raycastHit.position = pxRaycastHit.position;
                raycastHit.collider = GetCollider((PxActor*)pxRaycastHit.actor, pxRaycastHit.shape);
                raycastHit.faceIndex = (int)pxRaycastHit.faceIndex;
                raycastHit.flags = pxRaycastHit.flags;
                raycastHit.normal = pxRaycastHit.normal;
                raycastHit.u = pxRaycastHit.u;
                raycastHit.v = pxRaycastHit.v;
                raycastHits[i] = raycastHit;
            }
            return result;
        }

        public int OverlapAllBox(Vector3 position, Vector3 halfExtents, Quaternion rotation, OverlapHit[] overlapHits, int mask)
        {
            _sweepBoxCollider.halfExtents = halfExtents;
            return OverlapAll(_sweepBoxCollider, position, rotation, overlapHits, mask);
        }

        public int OverlapAllSphere(Vector3 position, float radius, OverlapHit[] overlapHits, int mask)
        {
            _sweepSphereCollider.radius = radius;
            return OverlapAll(_sweepSphereCollider, position, Quaternion.identity, overlapHits, mask);
        }

        public bool Overlap(Collider collider, Vector3 position, Quaternion rotation, out OverlapHit overlapHit, int mask)
        {
            if (collider.shape == null)
            {
                throw new NullReferenceException();
            }
            var geometry = PxShape_getGeometry(collider.shape);
            var pose = collider.GetQueryPose(position, rotation);
            PxOverlapHit pxOverlapHit = default;
            var filterData = PxQueryFilterData_new();
            filterData.flags |= PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter;
            filterData.data.word0 = (uint)mask;
            var result = PxSceneQueryExt_overlapAny(_scene, geometry, &pose, &pxOverlapHit, &filterData, _blockingQueryFilterCallback);
            overlapHit.collider = GetCollider((PxActor*)pxOverlapHit.actor, pxOverlapHit.shape);
            overlapHit.faceIndex = (int)pxOverlapHit.faceIndex;
            return result;
        }

        public int OverlapAll(Collider collider, Vector3 position, Quaternion rotation, OverlapHit[] overlapHits, int mask)
        {
            if (collider.shape == null)
            {
                throw new NullReferenceException();
            }
            if (overlapHits == null)
            {
                throw new ArgumentNullException(nameof(overlapHits));
            }
            if (overlapHits.Length > maxHits)
            {
                throw new IndexOutOfRangeException("Please increase the MaxHits constant");
            }
            var geometry = PxShape_getGeometry(collider.shape);
            var pose = collider.GetQueryPose(position, rotation);
            var filterData = PxQueryFilterData_new();
            filterData.flags |= PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter;
            filterData.data.word0 = (uint)mask;
            var pxOverlapHits = stackalloc PxOverlapHit[maxHits];
            var result = PxSceneQueryExt_overlapMultiple(_scene, geometry, &pose, pxOverlapHits, (uint)overlapHits.Length, &filterData, _nonBlockingQueryFilterCallback);
            for (var i = 0; i < result; i++)
            {
                var pxOverlapHit = pxOverlapHits[i];
                OverlapHit overlapHit = default;
                overlapHit.collider = GetCollider((PxActor*)pxOverlapHit.actor, pxOverlapHit.shape);
                overlapHit.faceIndex = (int)pxOverlapHit.faceIndex;
                overlapHits[i] = overlapHit;
            }
            return result;
        }

        public bool Sweep(Collider collider, Vector3 position, Quaternion rotation, Vector3 direction, float distance, out SweepHit sweepHit, int mask, bool hitTriggers = false, float inflation = 0f)
        {
            if (collider.shape == null)
            {
                throw new NullReferenceException();
            }
            if (float.IsInfinity(distance))
            {
                distance = float.MaxValue;
            }
            direction.Normalize();
            var geometry = PxShape_getGeometry(collider.shape);
            var pose = collider.GetQueryPose(position, rotation);
            var outputFlags = PxHitFlags.Default | PxHitFlags.Position | PxHitFlags.Normal | PxHitFlags.Uv | PxHitFlags.PreciseSweep;
            var filterData = PxQueryFilterData_new();
            filterData.flags |= PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter | PxQueryFlags.Postfilter;
            filterData.data.word0 = (uint)mask;
            PxSweepHit pxSweepHit = default;
            var result = PxSceneQueryExt_sweepSingle(_scene, geometry, &pose, (PxVec3*)&direction, distance, outputFlags, &pxSweepHit, &filterData, _blockingQueryFilterCallback, null, inflation);
            sweepHit = default;
            sweepHit.collider = GetCollider((PxActor*)pxSweepHit.actor, pxSweepHit.shape);
            sweepHit.distance = pxSweepHit.distance;
            sweepHit.faceIndex = (int)pxSweepHit.faceIndex;
            sweepHit.flags = pxSweepHit.flags;
            sweepHit.normal = pxSweepHit.normal;
            sweepHit.position = pxSweepHit.position;
            return result;
        }

        public int SweepAll(Collider collider, Vector3 position, Quaternion rotation, Vector3 direction, float distance, SweepHit[] sweepHits, int mask, out bool outBlockingHit, bool hitTriggers = false, float inflation = 0f)
        {
            if (collider.shape == null)
            {
                throw new NullReferenceException();
            }
            if (sweepHits == null)
            {
                throw new ArgumentNullException(nameof(sweepHits));
            }
            if (float.IsInfinity(distance))
            {
                distance = float.MaxValue;
            }
            direction.Normalize();
            var geometry = PxShape_getGeometry(collider.shape);
            var pose = collider.GetQueryPose(position, rotation);
            var outputFlags = PxHitFlags.Default | PxHitFlags.Position | PxHitFlags.Normal | PxHitFlags.Uv | PxHitFlags.PreciseSweep;
            var filterData = PxQueryFilterData_new();
            filterData.flags |= PxQueryFlags.Static | PxQueryFlags.Dynamic | PxQueryFlags.Prefilter;
            filterData.data.word0 = (uint)mask;
            var blockingHit = false;
            var hitCapacity = Math.Min(sweepHits.Length, maxHits);
            var pxSweepHits = stackalloc PxSweepHit[maxHits];
            var result = PxSceneQueryExt_sweepMultiple(_scene, geometry, &pose, (PxVec3*)&direction, distance, outputFlags, pxSweepHits, (uint)hitCapacity, &blockingHit, &filterData, _nonBlockingQueryFilterCallback, null, inflation);
            outBlockingHit = blockingHit;
            for (var i = 0; i < result; i++)
            {
                var pxHit = pxSweepHits[i];
                SweepHit sweepHit = default;
                sweepHit.collider = GetCollider((PxActor*)pxHit.actor, pxHit.shape);
                sweepHit.distance = pxHit.distance;
                sweepHit.faceIndex = (int)pxHit.faceIndex;
                sweepHit.flags = pxHit.flags;
                sweepHit.normal = pxHit.normal;
                sweepHit.position = pxHit.position;
                sweepHits[i] = sweepHit;
            }
            return result;
        }

        public bool BoxCast(Vector3 center, Vector3 halfExtents, Vector3 direction, Quaternion rotation, float distance, out SweepHit sweepHit, int mask, bool hitTriggers = false, float inflation = 0f)
        {
            _sweepBoxCollider.halfExtents = halfExtents;
            return Sweep(_sweepBoxCollider, center, rotation, direction, distance, out sweepHit, mask, hitTriggers, inflation);
        }


        public int BoxCastAll(Vector3 center, Vector3 halfExtents, Vector3 direction, Quaternion rotation, float distance, SweepHit[] sweepHits, int mask, out bool outBlockingHit, bool hitTriggers = false, float inflation = 0f)
        {
            _sweepBoxCollider.halfExtents = halfExtents;
            return SweepAll(_sweepBoxCollider, center, rotation, direction, distance, sweepHits, mask, out outBlockingHit, hitTriggers, inflation);
        }

        public int SphereCastAll(Vector3 center, float radius, Vector3 direction, Quaternion rotation, float distance, SweepHit[] sweepHits, int mask, out bool outBlockingHit, bool hitTriggers = false, float inflation = 0f)
        {
            _sweepSphereCollider.radius = radius;
            return SweepAll(_sweepSphereCollider, center, rotation, direction, distance, sweepHits, mask, out outBlockingHit, hitTriggers, inflation);
        }

        public void BakeMesh(Mesh mesh, bool convex)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }
            if (convex ? _convexMeshes.ContainsKey(mesh) : _triangleMeshes.ContainsKey(mesh))
            {
                return;
            }
            var meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            try
            {
                var meshData = meshDataArray[0];
                var positionStream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
                var vertexData = meshData.GetVertexData<byte>(positionStream);
                var positionOffset = meshData.GetVertexAttributeOffset(VertexAttribute.Position);
                var positionStride = meshData.GetVertexBufferStride(positionStream);
                var positionFormat = meshData.GetVertexAttributeFormat(VertexAttribute.Position);
                var positionDim = meshData.GetVertexAttributeDimension(VertexAttribute.Position);
                if (positionFormat != VertexAttributeFormat.Float32 || positionDim != 3)
                {
                    throw new Exception("Invalid vertex position format for PhysX mesh");
                }
                if (!convex)
                {
                    var desc = PxTriangleMeshDesc_new();
                    PxTriangleMeshDesc_setToDefault_mut(&desc);
                    desc.points.count = (uint)meshData.vertexCount;
                    desc.points.stride = (uint)positionStride;
                    desc.points.data = (byte*)vertexData.GetUnsafeReadOnlyPtr() + positionOffset;
                    if (meshData.indexFormat == IndexFormat.UInt16)
                    {
                        var indices = meshData.GetIndexData<ushort>();
                        desc.triangles.stride = (uint)sizeof(ushort) * 3;
                        desc.triangles.count = (uint)(indices.Length / 3);
                        desc.triangles.data = indices.GetUnsafeReadOnlyPtr();
                        desc.flags = PxMeshFlags.E16BitIndices;
                    }
                    else
                    {
                        var indices = meshData.GetIndexData<int>();
                        desc.triangles.stride = (uint)sizeof(int) * 3;
                        desc.triangles.count = (uint)(indices.Length / 3);
                        desc.triangles.data = indices.GetUnsafeReadOnlyPtr();
                        desc.flags = 0;
                    }
                    var cookingParams = PxCookingParams_new((PxTolerancesScale*)Unsafe.AsPointer(ref _toleranceScale));
                    var cookResult = PxTriangleMeshCookingResult.Failure;
                    var triMesh = phys_PxCreateTriangleMesh(&cookingParams, &desc, phys_PxGetStandaloneInsertionCallback(), &cookResult);
                    if (cookResult == PxTriangleMeshCookingResult.Failure || triMesh == null)
                    {
                        Debug.LogWarning("PhysX triangle mesh cooking failed");
                        return;
                    }
                    if (cookResult == PxTriangleMeshCookingResult.LargeTriangle)
                    {
                        Debug.LogWarning($"Mesh '{mesh.name}' has large triangles. Physics might not work correctly");
                    }
                    _triangleMeshes.Add(mesh, (IntPtr)triMesh);
                }
                else
                {
                    var desc = PxConvexMeshDesc_new();
                    PxConvexMeshDesc_setToDefault_mut(&desc);
                    desc.points.count = (uint)meshData.vertexCount;
                    desc.points.stride = (uint)positionStride;
                    desc.points.data = (byte*)vertexData.GetUnsafeReadOnlyPtr() + positionOffset;
                    desc.flags = PxConvexFlags.ComputeConvex;
                    var cookingParams = PxCookingParams_new((PxTolerancesScale*)Unsafe.AsPointer(ref _toleranceScale));
                    cookingParams.meshPreprocessParams = PxMeshPreprocessingFlags.WeldVertices;
                    cookingParams.convexMeshCookingType = PxConvexMeshCookingType.Quickhull;
                    var cookResult = PxConvexMeshCookingResult.Failure;
                    var convexMesh = phys_PxCreateConvexMesh(&cookingParams, &desc, phys_PxGetStandaloneInsertionCallback(), &cookResult);
                    if (cookResult == PxConvexMeshCookingResult.Failure || convexMesh == null)
                    {
                        throw new Exception("PhysX convex mesh cooking failed");
                    }
                    _convexMeshes.Add(mesh, (IntPtr)convexMesh);
                }
            }
            finally
            {
                meshDataArray.Dispose();
            }
        }

        public PxTriangleMesh* GetTriangleMesh(Mesh mesh)
        {
            return _triangleMeshes.TryGetValue(mesh, out var ptr) ? (PxTriangleMesh*)ptr : null;
        }

        public PxConvexMesh* GetConvexMesh(Mesh mesh)
        {
            return _convexMeshes.TryGetValue(mesh, out var ptr) ? (PxConvexMesh*)ptr : null;
        }

        public int GetCollisionMask(int layer)
        {
            return _collisionMasks[layer];
        }

        public void IgnoreCollision(Collider colliderA, Collider colliderB, bool ignore = true)
        {
            if (colliderA == null || colliderB == null || colliderA == colliderB)
            {
                return;
            }

            var pair = new ColliderPairKey((uint)colliderA.GetInstanceID(), (uint)colliderB.GetInstanceID());
            if (ignore)
            {
                _ignoredColliderCollisions.Add(pair);
            }
            else
            {
                _ignoredColliderCollisions.Remove(pair);
            }
        }

        public bool GetIgnoreCollision(Collider colliderA, Collider colliderB)
        {
            if (colliderA == null || colliderB == null || colliderA == colliderB)
            {
                return false;
            }

            var pair = new ColliderPairKey((uint)colliderA.GetInstanceID(), (uint)colliderB.GetInstanceID());
            return _ignoredColliderCollisions.Contains(pair);
        }

        public bool GetIgnoreCollision(uint colliderIdA, uint colliderIdB)
        {
            if (colliderIdA == 0u || colliderIdB == 0u || colliderIdA == colliderIdB)
            {
                return false;
            }

            var pair = new ColliderPairKey(colliderIdA, colliderIdB);
            return _ignoredColliderCollisions.Contains(pair);
        }
        private static string PtrToStringASCII(sbyte* ptr) => Marshal.PtrToStringAnsi((IntPtr)ptr);

        private static bool FilterByLayer(PxFilterData* queryFilterData, PxShape* shape)
        {
            var shapeFilterData = PxShape_getQueryFilterData(shape);
            return (queryFilterData->word0 & shapeFilterData.word0) != 0;
        }

        private static int TrailingZeroCount(uint value)
        {
            if (value == 0)
            {
                return 32;
            }
            var count = 0;
            while ((value & 1u) == 0u)
            {
                value >>= 1;
                count++;
            }
            return count;
        }


        [MonoPInvokeCallback(typeof(PreFilterDelegate))]
        private static PxQueryHitType BlockingPreFilter(PxRigidActor* rigidActor, PxFilterData* queryFilterData, PxShape* shape, uint hitFlags, void* userData)
        {
            var result = FilterByLayer(queryFilterData, shape);
            return result ? PxQueryHitType.Block : PxQueryHitType.None;
        }

        [MonoPInvokeCallback(typeof(PostFilterDelegate))]
        private static PxQueryHitType BlockingPostFilter(PxFilterData* filterData, PxQueryHit* hit, void* userData)
        {
            var raycastHit = (PxRaycastHit*)hit;
            return raycastHit->distance <= InitialOverlapEpsilon ? PxQueryHitType.None : PxQueryHitType.Block;
        }

        [MonoPInvokeCallback(typeof(PreFilterDelegate))]
        private static PxQueryHitType NonBlockingPreFilter(PxRigidActor* rigidActor, PxFilterData* queryFilterData, PxShape* shape, uint hitFlags, void* userData)
        {
            var result = FilterByLayer(queryFilterData, shape);
            return result ? PxQueryHitType.Touch : PxQueryHitType.None;
        }

        [MonoPInvokeCallback(typeof(ContactDelegate))]
        private static void Contact(void* userData, PxContactPairHeader* pairHeader, PxContactPair* contactPairs, uint nbPairs)
        {
            if (instance == null || pairHeader == null)
            {
                return;
            }
            var actor0 = pairHeader->actor0;
            var actor1 = pairHeader->actor1;
            for (uint i = 0; i < nbPairs; i++)
            {
                var pair = contactPairs[i];
                var removed = (int)pair.flags & ((int)PxContactPairFlags.RemovedShape0 | (int)PxContactPairFlags.RemovedShape1);
                if (removed != 0)
                {
                    continue;
                }
                var collider0 = instance.GetCollider(actor0, pair.shape0);
                var collider1 = instance.GetCollider(actor1, pair.shape1);
                if (collider0 == null || collider1 == null)
                {
                    continue;
                }
                var events = (int)pair.events;
                string message;
                if ((events & (int)PxPairFlags.NotifyTouchFound) != 0)
                {
                    if (OnPhysXCollisionEnter != null)
                    {
                        OnPhysXCollisionEnter(collider0, collider1);
                        continue;
                    }
                    message = "OnPhysXCollisionEnter";
                }
                else if ((events & (int)PxPairFlags.NotifyTouchPersists) != 0)
                {
                    if (OnPhysXCollisionStay != null)
                    {
                        OnPhysXCollisionStay(collider0, collider1);
                        continue;
                    }
                    message = "OnPhysXCollisionStay";
                }
                else if ((events & (int)PxPairFlags.NotifyTouchLost) != 0)
                {
                    if (OnPhysXCollisionExit != null)
                    {
                        OnPhysXCollisionExit(collider0, collider1);
                        continue;
                    }
                    message = "OnPhysXCollisionExit";
                }
                else
                {
                    continue;
                }
                collider0.gameObject.SendMessage(message, collider1, SendMessageOptions.DontRequireReceiver);
                collider1.gameObject.SendMessage(message, collider0, SendMessageOptions.DontRequireReceiver);
            }
        }

        [MonoPInvokeCallback(typeof(TriggerDelegate))]
        private static void Trigger(void* userData, PxTriggerPair* pairs, uint count)
        {
            for (uint i = 0; i < count; i++)
            {
                var pair = pairs[i];
                var removed = (int)pair.flags & ((int)PxTriggerPairFlags.RemovedShapeTrigger | (int)PxTriggerPairFlags.RemovedShapeOther);
                if (removed != 0)
                {
                    continue;
                }
                var triggerCollider = instance.GetCollider(pair.triggerActor, pair.triggerShape);
                var otherCollider = instance.GetCollider(pair.otherActor, pair.otherShape);
                if (triggerCollider == null || otherCollider == null)
                {
                    continue;
                }
                string message;
                switch (pair.status)
                {
                    case PxPairFlag.NotifyTouchFound:
                        if (OnPhysXTriggerEnter != null)
                        {
                            OnPhysXTriggerEnter(triggerCollider, otherCollider);
                            return;
                        }
                        message = "OnPhysXTriggerEnter";
                        break;
                    case PxPairFlag.NotifyTouchLost:
                        if (OnPhysXTriggerExit != null)
                        {
                            OnPhysXTriggerExit(triggerCollider, otherCollider);
                            return;
                        }
                        message = "OnPhysXTriggerExit";
                        break;
                    default:
                        message = null;
                        break;
                }
                if (message == null)
                {
                    continue;
                }
                triggerCollider.gameObject.SendMessage(message, otherCollider, SendMessageOptions.DontRequireReceiver);
                otherCollider.gameObject.SendMessage(message, triggerCollider, SendMessageOptions.DontRequireReceiver);
            }
        }

        [MonoPInvokeCallback(typeof(CustomFilterShaderDelegate))]
        private static PxFilterFlags CustomFilterShader(FilterShaderCallbackInfo* callbackInfo)
        {
            var isIgnoredColliderPair = instance.GetIgnoreCollision(callbackInfo->filterData0.word1, callbackInfo->filterData1.word1);
            if (isIgnoredColliderPair)
            {
                *callbackInfo->pairFlags = 0;
                return PxFilterFlags.Suppress;
            }

            var layer0 = TrailingZeroCount(callbackInfo->filterData0.word0);
            var layer1 = TrailingZeroCount(callbackInfo->filterData1.word0);
            var isIgnored = instance._ignoredLayerCollisions.Contains(new LayerPairKey(layer0, layer1));
            if (isIgnored)
            {
                *callbackInfo->pairFlags = 0;
                return PxFilterFlags.Suppress;
            }
            var isTriggerPair = phys_PxFilterObjectIsTrigger(callbackInfo->attributes0) || phys_PxFilterObjectIsTrigger(callbackInfo->attributes1);
            if (isTriggerPair)
            {
                *callbackInfo->pairFlags = PxPairFlags.TriggerDefault;
                return 0;
            }
            var isKinematic0 = phys_PxFilterObjectIsKinematic(callbackInfo->attributes0);
            var isKinematic1 = phys_PxFilterObjectIsKinematic(callbackInfo->attributes1);
            if (isKinematic0 || isKinematic1)
            {
                *callbackInfo->pairFlags = PxPairFlags.DetectDiscreteContact | PxPairFlags.NotifyTouchFound | PxPairFlags.NotifyTouchPersists | PxPairFlags.NotifyTouchLost;
                return 0;
            }
            *callbackInfo->pairFlags = PxPairFlags.DetectDiscreteContact | PxPairFlags.DetectCcdContact | PxPairFlags.SolveContact;
            return 0;
        }

        [MonoPInvokeCallback(typeof(ReportErrorDelegate))]
        private static void ReportError(PxErrorCode code, sbyte* message, sbyte* file, uint line, void* userData) => Debug.LogError($"PhysX error [{code}]: {PtrToStringASCII(message)} at {PtrToStringASCII(file)}:{line}");
    }
}
