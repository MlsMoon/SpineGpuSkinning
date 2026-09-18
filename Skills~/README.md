# Agent skills

Two skills ship with this plugin. The folder is named `Skills~` so Unity ignores it under `Assets/`.

| Skill | When to load |
| --- | --- |
| [gpuspine-use-plugin](gpuspine-use-plugin/SKILL.md) | Integrate, configure, troubleshoot, custom shader / RenderPass |
| [gpuspine-develop-plugin](gpuspine-develop-plugin/SKILL.md) | Edit plugin Runtime/ or Editor/ |

Moon Game Dev Tool Manager installs a thin host routing skill
(`spine-gpu-skinning-skill` from `../.mlsmoon/`). That router only points here.
Edit these source skills, not the router. Do not copy `Skills~/` into the host
`.agents/skills` as standalone skills.
Standalone clone of this repo: read [`../AGENTS.md`](../AGENTS.md) first; many agents already load that file.
Agent-facing text (`SKILL.md`, references, git commits) is English.
