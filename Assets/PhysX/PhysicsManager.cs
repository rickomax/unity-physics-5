using AOT;
using MagicPhysX;
using System;
using System.Collections;
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
        private delegate void ContactDelegate(void* unk, PxContactPairHeader* pairHeader, PxContactPair* contactPair, uint nbPairs);

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

        public struct CollisionKey
        {
            public int layerA;
            public int layerB;

            public CollisionKey(int layerA, int layerB) { this.layerA = layerA; this.layerB = layerB; }

            public override bool Equals(object obj) => obj is CollisionKey other && ((layerA == other.layerA && layerB == other.layerB) || (layerA == other.layerB && layerB == other.layerA));

            public override int GetHashCode() => layerA < layerB ? (layerA * 31 + layerB) : (layerB * 31 + layerA);
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

        private HashSet<CollisionKey> _ignoredLayerCollisions;
        private Dictionary<int, string> _layerNames;
        private int[] _collisionMasks;
        private float _nextUpdateTime;
        private int _nextId = 1;

        public static PhysicsManager instance { get; private set; }
        public static bool isShuttingDown { get; private set; }
        public Rigidbody dummyRigidbody => _dummyRigidbody;
        public float updateInterval => _updateInterval == 0f ? Time.fixedDeltaTime : _updateInterval;
        public PxPhysics* physics => _physics;

        private void Awake()
        {
            instance = this;
            InitialSetup();
            CreatePhysics();
            CreateScene();
            CreateCallbacks();
            CreateControllerManager();
            CreateDummyRigidbody();
            StartCoroutine(UpdatePhysics());
        }

        public void OnDestroy()
        {
            instance = null;
            isShuttingDown = true;
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
            _dispatcher = phys_PxDefaultCpuDispatcherCreate(1, null, PxDefaultCpuDispatcherWaitForWorkMode.WaitForWork, 0);
            _sceneDesc->cpuDispatcher = (PxCpuDispatcher*)_dispatcher;
            _sceneDesc->kineKineFilteringMode = PxPairFilteringMode.Keep;
            _sceneDesc->staticKineFilteringMode = PxPairFilteringMode.Keep;
            _sceneDesc->broadPhaseType = PxBroadPhaseType.Sap;
            _sceneDesc->flags |= PxSceneFlags.EnablePcm;
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
            PxControllerManager_setOverlapRecoveryModule_mut(_controllerManager, true);
        }

        private void CreateDummyRigidbody()
        {
            _dummyRigidbody = GetComponent<Rigidbody>();
            if (_dummyRigidbody == null)
            {
                _dummyRigidbody = gameObject.AddComponent<Rigidbody>();
            }
            _dummyRigidbody.InitializeAsDummyStatic();
        }

        private void BuildLayerCollisionDictionary()
        {
            _ignoredLayerCollisions = new HashSet<CollisionKey>();
            _collisionMasks = new int[layerCount];
            for (var layer1 = 0; layer1 < layerCount; layer1++)
            {
                for (var layer2 = 0; layer2 < layerCount; layer2++)
                {
                    if (Physics.GetIgnoreLayerCollision(layer1, layer2))
                    {
                        _ignoredLayerCollisions.Add(new CollisionKey(layer1, layer2));
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

        private IEnumerator UpdatePhysics()
        {
            _nextUpdateTime = Time.time + updateInterval;
            while (true)
            {
                if (Time.time < _nextUpdateTime)
                {
                    yield return null;
                }
                UpdateInternal();
                _nextUpdateTime = Time.time + updateInterval;
            }
        }

        private void UpdateInternal()
        {
            if (_scene == null)
            {
                throw new NullReferenceException();
            }
            PxScene_simulate_mut(_scene, Time.fixedDeltaTime, null, _scratchBuffer, scratchBlockSize, true);
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
        private static void Contact(void* unk, PxContactPairHeader* pairHeader, PxContactPair* contactPairs, uint nbPairs) { }

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
                var message = pair.status == PxPairFlag.NotifyTouchFound ? "OnPhysXTriggerEnter" : pair.status == PxPairFlag.NotifyTouchLost ? "OnPhysXTriggerExit" : null;
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
            PxPairFlags flags;
            if (phys_PxFilterObjectIsTrigger(callbackInfo->attributes0) || phys_PxFilterObjectIsTrigger(callbackInfo->attributes1))
            {
                flags = PxPairFlags.TriggerDefault;
            }
            else
            {
                flags = PxPairFlags.ContactDefault;
            }
            var layer0 = TrailingZeroCount(callbackInfo->filterData0.word0);
            var layer1 = TrailingZeroCount(callbackInfo->filterData1.word0);
            var isIgnored = instance._ignoredLayerCollisions.Contains(new CollisionKey(layer0, layer1));
            *callbackInfo->pairFlags = flags;
            return isIgnored ? PxFilterFlags.Suppress : 0;
        }

        [MonoPInvokeCallback(typeof(ReportErrorDelegate))]
        private static void ReportError(PxErrorCode code, sbyte* message, sbyte* file, uint line, void* userData) => Debug.LogError($"PhysX error [{code}]: {PtrToStringASCII(message)} at {PtrToStringASCII(file)}:{line}");
    }
}