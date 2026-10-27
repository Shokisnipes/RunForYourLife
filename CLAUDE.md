# Run For Your Life

Co-op first-person survival horror in **Unity 6 (6000.5.9f1), URP, Netcode for GameObjects**. Friends fortify a building
before night; an unknown monster arrives. Today's monster is a 14 m giant that stalks foggy streets, can't fit inside
buildings, peeks and reaches through doorways, and grabs players. The full game design (monster genome, evolution
across runs, scouts, the Mimic, base building, risks, prototype plan) is in `Docs/DESIGN.md` - read it when a task
touches gameplay design. The build plan is `Docs/TASKS.md`: always follow its **Token rules**, and tick a task
when it's done and tested.

## How the code fits together (Assets/Scripts)
- **Networking:** host runs all monster logic. Player prefab = `FirstPersonController` (owner moves itself via
  `OwnerNetworkTransform`; crouch/pitch/grabbed synced with owner-write NetworkVariables). `NetworkUI` = main/pause menu
  (Host/Join/Quit), lives on the NetworkManager object. `NetworkUI.MenuOpen` blocks player input.
- **Monster:** `MonsterAI` (host only; states Patrol, Investigate, Notice, Chase, Reach, Search; giant NavMesh agent
  type "Giant"; syncs state/look/hand targets for everyone). `MonsterAnimator` (on the Model child, runs on every
  client: locomotion blend, stride matching, foot plants -> footsteps/shake, head look IK, reach/carry IK, floor snap).
  `MonsterAudio` (breathing, footsteps, growls, scream, chase music, heartbeat, death sounds). `PoseAdjuster` (per-bone
  rotation tweaks). `ReachPoint` = doorway marker the giant crouches at.
- **Climber** (Climber genome; the giant hides): `ClimberAI` (host only; crawls the streets on NavMesh agent type
  "Climber", too tall for doorways; scripted climb up/down walls to the roof, drops in and climbs out through `RoofHatch`) + `ClimberAnimator` on its Model child + `ClimberAudio` (steps timed to the anim loop, call, grab, hatch creak).
  Each monster only shows up when the genome's Access matches it (synced `netInPlay`).
- **Senses:** `NoiseSystem.Emit(pos, radius)`; `FootstepNoise` sends player steps to the host by RPC. Flashlight on or
  standing under a lit `StreetLamp` makes players visible from further.
- **Feel:** `CameraShake` (on the player camera), `Flashlight` (procedural cookie), head bob in FirstPersonController.
- **World:** `BoxUV` gives scaled cubes real-world texture tiling. `AmbientAudio`, `WindowFlicker`, `LobbyCameraDrift`.

## Editor tools (Tools > RunForYourLife)
Building Generator (adds a roof hatch), Upgrade Existing Buildings, Add Roof Hatches To Existing Buildings, Widen Interior Doorways, Add Reach Points To Existing Buildings, Building Texturer (Use Texture
Set), Street Lamps, Monster Animator Builder, Climber Setup (clips, agent type, controller, scene object, NavMesh bake),
Make It Look Good. Prefer improving these tools over hand-editing scenes.

## Conventions and lessons learned
- Expose tuning values in the Inspector with tooltips. Comment scripts in plain language.
- Only the host runs MonsterAI; visual/audio effects run on each client from synced state.
- Giant NavMesh: agent type "Giant" (radius 2.5, height 14). Re-bake after moving buildings. NavMesh floats above
  the floor; MonsterAnimator snaps the model to the real floor.
- Mixamo clips: Rig = Humanoid; Loop Time on looping clips; Root Transform all Bake Into Pose, Position (Y) Based Upon
  Feet; rename clips from "mixamo.com" before using them.
- ProBuilder shapes: change Shape Size, keep Transform scale at 1.
- Git: always `git pull --no-edit` before `git push`. Binary assets (png/jpg/fbx/wav) go through Git LFS.

## Assets: what exists and what's still needed
Only ONE monster model exists: the giant (a Mixamo humanoid scaled 8x, Model child of the Monster object).
**When a task needs a model, animation or sound that isn't in the project, stop and tell Petar exactly what to get,
where from, and the import settings, before writing code that depends on it.**

| For | Needed | Where / what to pick | Notes |
| --- | --- | --- | --- |
| Climber (prototype step 4) | Lanky humanoid + climb, crawl, drop-down, idle | Mixamo: a thin character; anims "Climbing", "Crawl", "Jumping Down", "Zombie Idle" | Humanoid rig, ~3-4 m tall (scale ~2). Same import settings as the giant. |
| Scouts (prototype step 6) | Small creature + run, sniff/idle, attack, death | Mixamo crawling humanoid ("Crawl", "Zombie Crawl") scaled ~0.6, or a free animated creature from Quaternius (CC0) | Must visibly show the genome: eyes/ears variants via swapped materials or child meshes. |
| Giant low peek | Prone / crawl pose | Mixamo "prone" or "crawl", frozen to 1-2 frames (Start/End in the clip settings) | Goes in the Peek state. |
| Near miss / captured | Ground slam, struggle, scream sounds | freesound.org (CC0 filter) | Force To Mono for 3D sounds. |
| Night job | Generator loop, refuel sounds; a generator model | freesound.org; Kenney or Quaternius props (CC0) | |
| Burrower, swarm, phaser | Later, after the prototype | | Don't build these yet (see Docs/DESIGN.md, Scope). |
