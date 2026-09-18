---
name: spine-gpu-skinning-skill
description: Route SpineGpuSkinning host agents to the packaged source skills. Covers the use vs develop split and pointers to xx host cases. Use when choosing which skill to read; edit Skills~/ for bake, integration, or batch contracts.
---

# GpuSpine: Host Router

> **This file only routes. Keep it thin.** Do not put host-project facts, paths, or
> verification logs here.
>
> Durable contracts and xx cases belong in the **source skills** (edit those):
> - Integrate / troubleshoot: [`Skills~/gpuspine-use-plugin/SKILL.md`](../../../Skills~/gpuspine-use-plugin/SKILL.md)
> - Edit plugin source: [`Skills~/gpuspine-develop-plugin/SKILL.md`](../../../Skills~/gpuspine-develop-plugin/SKILL.md)
> - Current contract overrides older docs: [`AGENTS.md`](../../../AGENTS.md)
>
> Do not copy `Skills~/` into the host `.agents/skills` as standalone skills.

## Which skill to read

| Task | Read |
|---|---|
| Add the component, manual bake, skins, custom shader / RenderPass, CPU fallback | `gpuspine-use-plugin` |
| Edit `Runtime/` or `Editor/`, batches, fingerprint, buffer contracts | `gpuspine-develop-plugin` |

Paths are relative to the plugin root `SpineGpuSkinning/`. Use the host install path.

## xx cases live in the source skills

Fictional host **xx** / character **Xx** cases are split under source `references/`:

| Topic | File |
|---|---|
| Integration list and host glue | `Skills~/gpuspine-use-plugin/references/host-integration-xx.md` |
| Custom Pass redraw and overlay-skin A-B | `Skills~/gpuspine-use-plugin/references/custom-pass-redraw-xx.md` |
| Character bake options | `Skills~/gpuspine-use-plugin/references/character-setup-xx.md` |
| Batch and layout lifecycle | `Skills~/gpuspine-develop-plugin/references/batch-lifecycle-xx.md` |

Do not write real Prefab / shader / Pass / character names back into this router
or into the public source skills.

## Checklist

- [ ] Read the matching source skill and `AGENTS.md` before writing code
- [ ] Verify APIs in this repo; do not trust an old README
- [ ] Do not edit the Spine runtime; do not add Unity Tests
- [ ] Put new reusable patterns in source-skill `references/`, not here
