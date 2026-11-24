# RagdollField (Delirium Physics for Ravenfield)

Euphoria / NaturalMotion-style **Active Ragdoll** mod for **Ravenfield** (Unity 2020.3 Mono x64, BepInEx 5), powered by the native Rust physics engine from [delirium-physics-no-game](https://github.com/ronokopl/delirium-physics-no-game) in **No-Game mode** (no GTA IV assets required).

## Features

- **Native Delirium Physics Engine (`delirium_physics.dll`)**:
  - 21-part capsule body with 20 articulated motorized joints, 55 NaturalMotion behaviours, and fixed 60 Hz multi-threaded stepping.
  - Pure No-Game mode — runs completely standalone with Ravenfield.
- **Dynamic Combat Reactions**:
  - **Modded Weapon Force Normalizer**: Soft-knee (`log1p + tanh`) force normalization + per-limb velocity cap + sliding burst budget so everything from light pistols to extreme Workshop weapons and high-ROF miniguns/shotguns produces natural, twitch-free physics.
  - **Power-Scaled Body Lean & Hit-Zone Torque**: Upper-body hits tilt and whip the torso in the bullet direction proportionally to weapon power (`SMG` -> `Pistol` -> `Rifle` -> `Shotgun` -> `Sniper`), gut shots double soldiers over, and leg shots sweep legs out.
  - **Smooth Gun-to-Injury Transitions**: Dual-layer entry blending + upper-arm follow smoothly peels arms off the rifle-aiming pose into wound clutching and balance stepping while keeping the weapon tracked in the ragdoll hand (zero floating gun).
  - **Visceral Headshots & Zero A-Pose Deaths**: Two-stage `holdPose` collapse ensures fatal shots and 1-shot headshots whip naturally from the current pose without snapping into an A-pose.
  - **Death Agony (Final-Breath Movement)**: Configurable chance for non-headshot, non-explosion kills to exhibit 3 randomized styles of fading ground movement (gentle writhe, foetal curl, or upper-body reach) before going fully limp — with instant finish-off on double-tap.
  - **Explosions, Melee/Kick, High Falls, Near-Miss Flinch**.
- **Ravenfield Integration**:
  - **Reliable Native Get-Up**: Smoothly blends standing recoveries back into combat and hands fallen alive bots back to Ravenfield's native `Actor.GetUp()` animation with accurate face-up/face-down orientation.
  - **Player Safety**: Full active stagger & reactions on bots; player only enters Delirium when fallen or dead (preserves FPS aiming).
  - **LOD & Performance Manager**: Distance culling (default 60 m), concurrent active agent cap (default 18), and automatic corpse freezing (`DeadFrozen`) to keep large battles smooth.
  - **Workshop Custom Skin Support**: Automatic upright bind-pose extraction, `HumanBodyBones` + name-based bone mapping + configurable `BoneMap`, and background adaptive balance tuning per skin.
  - **Vehicles & Water**: Pre-impact vehicle bracing (`braceForImpact`), vehicle collisions (`nm_agent_car_hit`), and `WaterLevel` buoyancy.
  - **In-Game Test Panel (`F7`) & Test Range (`F8`)**: Live sliders, diagnostics, hit telemetry, reaction triggers, skeleton checker, and rig comparison dumper.

## Controls (Default Hotkeys)

| Key | Action |
|---|---|
| `F7` | Toggle **Delirium Test Panel** (live tuning, telemetry, reaction buttons, Reset Defaults) |
| `F8` | Toggle **Test Range Mode** (bots stand idle and do not fire) |
| `F9` | Push / ragdoll the bot under your crosshair |
| `F10` | Release all active Delirium ragdolls back to Ravenfield |

## Installation (Pre-built Release)

1. Install **BepInEx 5 (x64)** into your Ravenfield game directory.
2. Download the latest release zip from the **Releases** page and extract it into your Ravenfield folder so that:
   - `BepInEx/plugins/DeliriumRavenfield/DeliriumRavenfield.dll`
   - `BepInEx/plugins/DeliriumRavenfield/delirium_physics.dll`
   are present.
3. Launch Ravenfield. After first launch, customize settings in `BepInEx/config/com.fordg.deliriumravenfield.cfg` or press `F7` in-game.

## Building from Source

Requirements:
- `.NET SDK 8+` (targets `.NET Framework 4.7.1` via `Microsoft.NETFramework.ReferenceAssemblies`)
- Ravenfield installed (for `Assembly-CSharp.dll` / UnityEngine references)
- Rust (`x86_64-pc-windows-gnu` + MinGW-w64) if rebuilding `delirium_physics.dll` from [delirium-physics-no-game](https://github.com/ronokopl/delirium-physics-no-game)

Run from the repository root:
```powershell
.\build-and-deploy.ps1
```
Pass `-RebuildNative` to also recompile `delirium_physics.dll` from the sibling `delirium-physics-no-game` repository.

## License

Built on top of **Delirium Physics** ([delirium-physics-no-game](https://github.com/ronokopl/delirium-physics-no-game)), licensed under **GPL-3.0-only**. This repository is distributed under the **GNU General Public License v3.0 (GPL-3.0-only)** — see [LICENSE](LICENSE).
