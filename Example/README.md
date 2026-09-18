# GPU Spine Crowd Example

An independent, interactive CPU/GPU skinning comparison with two original characters:
**SampleDude**, a warm-colored adventurer, and **SampleRobot**, a teal companion.

## Run

1. Install Unity 2022.3+, URP 14 and spine-unity 4.2.
2. Open `GpuSpineComparison.unity` and enter Play Mode, or use **Tools / GPUSpineSkin / Play Comparison Example**. The menu restores your previous play-start scene on exit; no Player build is required.
3. Read the large **FPS** counter and **ms / frame** measurement at the top right.
4. Use **Switch to CPU / GPU** to switch the same live crowd without resetting its animations.
5. Choose **Mixed skins**, **Ember / Mint**, **Forest / Copper**, or **Midnight / Rose**.
6. Enter a count and press **Apply**, or choose **32 / 100 / 300 / 1000**.

The default is 32 complex couriers. The production profile uses the same high-complexity mesh at 300 instances so CPU Mesh work becomes visible in the comparison. Use **Simple duo / Complex courier / Ultra 100 bones** to select the workload. **Production 300** is a one-click 300-instance stress profile.
The simple duo splits the count evenly between adventurers and robots. Counts range
from 0 to 1000; odd counts give the adventurer one extra instance. Characters walk along
lanes with deterministic variation in speed, direction and starting animation phase.
The camera fits the crowd when the count or window aspect changes.

The UI uses IMGUI. No host game framework, UI kit, TextMesh Pro, input package,
renderer feature or game startup scene is required. Prefabs are saved inactive so references
and mode are configured before their first activation; enable them when using them manually.

## When to use GPU skinning

This plugin is a workload-dependent rendering option, not a universal FPS upgrade.
**CPU skinning can be cheaper for small crowds and low-vertex characters.** Bone evaluation
and AnimationState still run on the CPU in both modes. GPU mode adds palette/deform/color
uploads, visibility checks, sorting, buffer management and draw submission.

| Case | What to expect |
| --- | --- |
| Few characters, mostly region attachments, lightweight meshes | Start with the stock CPU path; GPU overhead can exceed the saved mesh work. |
| Many visible instances sharing meshes, skins and atlas materials | A candidate for GPU skinning; measure CPU mesh generation/upload savings against plugin overhead. |
| Dense weighted meshes and deforming cloth/accessories | More CPU vertex work can move to the GPU, but deform evaluation and data uploads remain costs. |
| Frequent skin/attachment/draw-order changes, many materials or interleaved transparency | Batches may split; more characters do not imply one draw. Measure real submissions and spikes. |
| Clipping, shadows or custom multipass rendering | GPU can support these paths, but each has additional cost and integration requirements. Verify visual parity first. |
| CPU animation/constraints, AI, fill-rate or other systems dominate | Moving mesh skinning alone may provide little or no frame-time benefit. |

The Example has two explicit workloads:

- **Simple duo**: original adventurer and robot, each 20 bones, 16 slots and 159 visible
  vertices, with three skins. This is a basic correctness/control sample, not proof of speedup.
- **Complex courier** (default): 48 bones, 24 slots, about 1170 baked vertices per skin,
  four deforming weighted cloth/accessory meshes, a 24-point non-convex visor clip,
  attachment/color/draw-order timelines and three skins. All of those animations run in
  the walk loop. Mesh density supports visible cloth deformation rather than duplicate geometry.


- **Ultra courier**: 100 bones, 26 slots and about 2490 baked vertices per skin. It extends
  the complex courier with segmented cloth, scarf, hair and two ribbon chains; these bones
  animate through the walk loop and participate in weighted Deform meshes. Use it when
  evaluating high bone-count and deform upload cost. It is intentionally a stress case,
  not a recommendation to use 100 bones for every production character.

The complex case exercises the same *kinds* of features as a production Spine character.
It does not reproduce any host game's full lighting, shadows, outline, camera or gameplay costs,
nor does it promise that GPU will win. Skeleton/skin/animation counts describe content;
only the active mesh, animations, clipping and rendering work determine per-frame cost.

Compare CPU and GPU with the **same case, count, skin distribution, resolution and camera**.
Allow warm-up and compare repeated FPS and frame-time samples. Check Profiler CPU mesh work,
render thread, GPU time, GC and actual draw count before choosing a mode. Do not infer lower
CPU consumption from FPS alone. Player results are preferable for final performance decisions;
Editor Play Mode is sufficient for interactive exploration but includes Editor overhead.

## Reading the comparison

- FPS and average frame time refresh every 0.5 seconds using unscaled elapsed time.
- Creation is spread across frames. Creation frames and a one-second warm-up are excluded.
- **GPU active: N / total** reports actual `IsGpuActive` instances. A fallback is visible.
- **Last CPU / Last GPU** retains the latest sample for each mode at the same count.
  Changing the count or skin clears both values. These are live samples, not benchmark averages.
- VSync and the frame cap are disabled while this scene runs and restored on destruction.
- Both modes still evaluate animation, constraints and bone transforms on the CPU. GPU mode
  moves mesh skinning and submission to the plugin. This small sample does not guarantee a
  higher GPU-mode FPS: crowd size, mesh complexity, draw order and hardware affect the result.
- For representative comparisons use a standalone Player, keep the same count and resolution,
  allow warm-up, and compare repeated samples. Editor activity affects Editor FPS.

## Assets and source

Each character has three actual Spine skins (default / forest / midnight for the adventurer;
default / copper / rose for the robot). Every skin has unique attachment identities for GPU baking.
Skin changes preserve animation time. The walk cycle uses authored foot-contact trajectories
with inverse-kinematics angles exported as regular bone timelines.

Each character has 20 bones, 16 slots, a 99-vertex weighted torso mesh and `idle` / `walk`
loops. PNG atlases use straight alpha; both CPU and GPU materials must preserve that setting.
The weighted torso blends hip and chest bones; rigid face and limb parts use region attachments.

`Source~/` contains editable SVG parts and `generate_characters.py`. With Python and Pillow,
run `generate_characters.py`, then `generate_complex_courier.py` to regenerate all source assets. Unity ignores the authoring folder; consumers need no Python installation.
These are Spine 4.2 runtime exports, not native `.spine` editor projects.

Save or close any untitled scenes first. After changing sources, run **Tools / GPUSpineSkin / Build Comparison Example**. The builder
locates its own folder, imports both characters, bakes GPU data, updates inactive prefabs and
saves the scene without replacing the user's current scene. Do not rebuild while that example
scene is already open; close it first so the saved version and open version cannot diverge.

Runtime interfaces are `GpuSpineExampleController.SetCount(int)` and
`SetGpuEnabled(bool)`, `SetComplexCase(bool)` and `SetSkinSelection(int)` (-1 mixed, 0–2 specific skins). `GpuSpineExampleWalker` owns per-character motion; the HUD only
presents the controller state and forwards user input.

## Baked data inspector

Generated fields on `GpuSpineBakedData` are read-only in the normal Inspector. You can still
expand entries and audit details. `DeclaredCombos` remains editable because it is a baking
input; use **Rebake from source** after changing it. No runtime serialization contract changed.



Editor note: the example restricts GPU submissions to its Game Camera through
`GpuSkeletonRenderer.CameraFilter`. This avoids paying the GPU batch preparation cost for
SceneView and preview cameras while editing. The CPU renderer remains visible in those views.
The plugin itself keeps multi-camera support; production integrations should set an explicit
filter when a renderer should belong to only one camera.

## License

The original character artwork, authoring source and example scripts are distributed under
the plugin's MIT license. Spine runtimes are external dependencies under their own license.

## Windows Player measurement

A same Player measurement at 1280×720 with 32 UltraCourier instances recorded about 84.5–92.7 FPS
on GPU (10.79–11.83 ms/frame) and 151.8 FPS on CPU (6.59 ms/frame). The GPU path was slower for
this workload because the saved CPU mesh rebuild did not cover bone/Deform uploads and batch
submission overhead. This excludes Editor SceneView/Inspector work and is only evidence for the
recorded machine, resolution, camera and count. Repeat on target hardware and multiple counts
before choosing GPU skinning.

## Editor automation

`GpuSpineExampleControl.Apply(Request)` exposes `status`, `count`, `gpu`, `skin`, `case`
and `freeze` actions with an integer `value`. Case 0 is simple, 1 is complex; skin -1 is mixed.
An editor CLI can put request JSON into `UnityEditor.SessionState` key
`GpuSpine.Example.Request`, execute **Tools / GPUSpineSkin / Apply Example Control Request**,
and read JSON from `GpuSpine.Example.Result`. Requests are consumed; no Selection or dialog
is required. State includes actual GPU instances and draw slices; changes settle over frames.
This API controls the same running example as the HUD and does not create another workload.
