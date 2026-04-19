## Unity PhysX 5.1.3 Wrapper

This repository contains a work-in-progress and experimental implementation of PhysX 5.1.3 for Unity.

This project was created to provide more direct control over PhysX parameters, as well as access to features not exposed by Unity’s built-in physics system, such as the PxBoxController.

The classes in this repository aim to mimic Unity’s native Physics API where possible, but currently only implement a subset required for internal use. The API may evolve over time.

Many PhysX features that Unity normally abstracts are exposed and configurable in this implementation.

Pull requests adding new features, classes, or improving API compatibility with Unity are welcome.

---

## Dependencies

This project relies on the native libraries and C# bindings available at:

- https://github.com/rickomax/phys-rs

---

## Current Limitations

The current version does not include:

- Joints or constraints
- Force, impulse, or torque application methods
- CapsuleCollider, WheelCollider (likely not supported by the underlying library at the moment), TerrainCollider
- Capsule Character Controller
- Hierarchies are not sync yet. Unity to Rigidbodies/Collider transforms should be sync manually (see more bellow)

---

## Platform Support

This library has been compiled and tested on Windows x64 only.

Other platforms are not currently supported or tested. Contributions to improve cross-platform support are welcome.

---

## Core Classes

### PhysicsManager

This is the central component of the system.

- Must be added to a GameObject before using any other physics features
- Handles internal PhysX simulation updates
- Provides shared functionality used by all other classes

Features:
- Optional connection to PhysX PVD (requires debug native libraries)
- Custom update interval via `_updateInterval`
  - `0` uses Unity’s FixedUpdate timing

---

### Queries

Several methods emulate Unity’s Physics API, with some differences:

#### Mesh Processing

- `BakeMesh`
  - Similar to Unity’s `Physics.BakeMesh`, but accepts a `Mesh` as input
  - Cooking options are not exposed yet (todo)

#### Penetration

- `ComputePenetration`
  - Equivalent to Unity’s `Physics.ComputePenetration`

#### Ray Queries

- `LineCast`
- `LineCastAll`
- `Raycast`
- `RaycastAll`

Differences from Unity:

- Uses a custom `RaycastHit` struct (not `UnityEngine.RaycastHit`)

---

### Sweeps and Overlaps

Unlike Unity, this implementation uses actual collider instances instead of primitive parameters.

Available methods:

- `Sweep`
- `SweepAll`
- `Overlap`
- `OverlapAll`

Details:

- Sweep:
  - Performs a sweep using a given collider
  - Returns the first `SweepHit`

- SweepAll:
  - Performs a sweep and fills a `SweepHit` array
  - Returns the hit count

- Overlap:
  - Performs an overlap test
  - Returns the first `OverlapHit`

- OverlapAll:
  - Performs an overlap test and fills an `OverlapHit` array
  - Returns the hit count

To retrieve UV coordinates from query results, use:
- `QueryExtensions.GetUV`
Trigger raycast/sweep/overlap not implemented yet

---

### Rigidbody

Mimics Unity’s `Rigidbody` component.

Important differences:

- PhysX actor transforms are independent from Unity transforms
- To modify rigidbodies position/rotation use the rigidbody `position` and `rotation` properties

Additional behavior:

- Can be made static via `physicsStatic`
- Can be made kinematic via `isKinematic`

---

### Colliders

The following collider types are implemented:

- `SphereCollider`
- `BoxCollider`
- `MeshCollider`

These behave similarly to Unity equivalents but are backed by PhysX shapes.
To modify colliders position/rotation use the collider `position` and `rotation` properties

---

### BoxCharacterController

A PhysX-based character controller using a box shape instead of a capsule.

Similar to Unity’s `CharacterController`, with:

- `Move` method only (no `SimpleMove` yet) (todo)
- Additional PhysX-specific features
- Supports an experimental PhysX feature allowing interaction with kinematic rigidbodies
- Behavior follows PhysX implementation

For more details, refer to:
https://nvidia-omniverse.github.io/PhysX/physx/5.1.3/docs/CharacterControllers.html

---

## Notes

- This project is experimental and under active development
- API stability is not guaranteed
- Designed primarily for advanced use cases requiring low-level control over PhysX
