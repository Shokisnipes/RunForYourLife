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
  (Host/Join/Quit), lives on the NetworkManager object. `NetworkUI.MenuOpen` blocks player input. Online play =
  Unity Relay (com.unity.services.multiplayer, anonymous sign-in): HOST gets a join code (clipboard + pause menu),
  JOIN takes the code; an IP typed into JOIN connects directly. Each game copy on one PC signs in with its own
  profile (hash of dataPath) so MPPM host + player 2 are different Relay players.
- **Monster:** `MonsterAI` (host only; states Patrol, Investigate, Notice, Chase, Reach, Search; giant NavMesh agent
  type "Giant"; syncs state/look/hand targets for everyone). `MonsterAnimator` (on the Model child, runs on every
  client: locomotion blend, stride matching, foot plants -> footsteps/shake, head look IK, reach/carry IK, floor snap).
  `MonsterAudio` (breathing, footsteps, growls, scream, chase music, heartbeat, death sounds). `PoseAdjuster` (per-bone
  rotation tweaks). `ReachPoint` = doorway marker the giant crouches at.
- **Climber** (Climber genome; the giant hides): `ClimberAI` (host only; crawls the streets on NavMesh agent type
  "Climber", too tall for doorways; scripted climb up/down walls to the roof, drops in and climbs out through `RoofHatch`; door camping: doorstep counts as inside, perches above the door and pounces, leaps off roofs when it sees you) + `ClimberAnimator` on its Model child + `ClimberAudio` (steps timed to the anim loop, call, grab, hatch creak).
  Each monster only shows up when the genome's Access matches it (synced `netInPlay`). `GenomeManager.bothMonsters`
  (on) picks a genome for each, so the giant and the Climber hunt the same night (Current = giant, Companion = Climber).
  A Climber catch kills on the spot with no timer (FPC.Down, netHiddenUntil = NoTimer); a teammate revives you with
  the same hold E as freeing. Nobody left standing still loses the run.
- **Scouts** (task 6, in progress): tiny (0.6 m) crawlers that relay player positions to the monster. Body =
  `Assets/Prefabs/Scout.prefab` from Tools > Scout Setup (Ch25 model scaled 0.32, Crawling + Prone Death clips, the
  Climber's wall climbs reused). `ScoutAnimator` (crawl playback from speed, lift so the body sits on the floor,
  screech head IK, death arm IK: hands flat by the shoulders instead of the clip's rifle pose). `ScoutAudio`
  (skitter loop, sniff, screech, death). `ScoutAI` (host brain, NetworkObject, Climber NavMesh: Wander,
  Investigate, Spotted, Screech, Flee, Frozen, Dead; flashlight beam freezes it, stomp RPC kills it).
  `ScoutDirector` on the NetworkManager object (registers the prefab, spawns GenomeManager.scoutCount when
  allowScouts, sends idle scouts toward players as calm builds, Relay() -> MonsterAI/ClimberAI.Relay, vague
  fallback hints with no scouts left, left-click stomp input + hint). Windows: BreakableWindow.State.Holed (procedural
  pane with a hole; never share vertices between the two faces or normals go NaN and bloom whites out the screen);
  scouts hop through holed/broken windows when a closed door blocks the path; climb walls onto flat roofs
  (ClimbUp/ClimbDown, scripted like the window hop). ScoutAnimator.ShowSense adds the genome's eyes/ears to the head.
  Giant running grab: MonsterAnimator stoop (hips drop in the IK pass, bow + arm re-solve in LateUpdate) so the hand
  really reaches you.
- **Evolution:** `GenomeManager` (NetworkManager object) picks the genome weighted by trait weights saved in
  `evolution.json` (persistentDataPath); monster win +weight, beaten -weight, host quitting mid-run = monster win.
- **Captured (task 8):** MonsterAI state Carry: after a catch the giant walks you to a `CityGenerator` HidingSpot
  (City.hidingSpots: parks, parking aisles, empty lots), stoops (MonsterAnimator.StartPutDown) and puts you down;
  ClimberAI leaves you where it caught you. FirstPersonController: Hide(spot) starts the host-synced death timer
  (netHiddenUntil, hiddenSurviveSeconds), Struggle() slows it, Free() releases you, DieRpc when it runs out.
  `PlayerRescue` (Player prefab): blood drips along the carry, hold E to free a teammate, Space to struggle
  (PlayerRagdoll.Wiggle). The ragdoll ignores monster colliders (the giant's body capsule used to fling it).
  GameFlow loses when nobody is free (all dead or hidden).
- **Voice:** `VoiceChat` on the Player prefab: always-on proximity voice (mute key M). Owner records the default mic,
  20 ms Opus frames (Concentus, Assets/Plugins) sent by unreliable RPC only while talking; others play it as a 3D
  sound from the speaker's head (a hidden player calls out from the hiding spot). Loud talking -> NoiseSystem.
- **Senses:** `NoiseSystem.Emit(pos, radius)`; `FootstepNoise` sends player steps to the host by RPC. Flashlight on or
  standing under a lit `StreetLamp` makes players visible from further.
- **Audio mix:** every 3D sound uses `AudioMix.Setup3D` (real inverse-distance falloff, silent at max range); the
  target level for each kind of sound is listed at the top of `AudioMix.cs`. 3D clips must be Force To Mono.
- **Player body:** `PlayerAnimator` on the Player's Model child (Martha, Assets/Characters/Martha), every client:
  reads movement from position changes, 2D strafe blend trees (stand/crouch) with clips at their natural speeds and
  playback sped up past them; your own neck/head is shrunk and the body slides back behind the camera (more when
  looking down); other players' heads follow their synced pitch. Built by Tools > Player Model Setup, which also
  turns each Mixamo loop so it travels the way its name says (table in PlayerModelSetup.cs) and places it in the
  blend tree at its measured speed. (The wall cover/peek system was removed on 2026-10-27:
  it fought the view behind short walls. The cover clips are still in the controller, unused.)
- **Feel:** `CameraShake` (on the player camera), `Flashlight` (procedural cookie), head bob in FirstPersonController.
- **Sky:** `SkyCycle` (on the Directional Light, every client, from the synced GameFlow clock) fades a fall evening
  (~8 pm, sun just set) into night over the prep phase: light, ambient probe, fog and the `RunForYourLife/Sky` shader
  (Assets/Shaders; gradient into fog, sunset glow, stars, drifting clouds, moon shaded as a sphere lit by the sun).
  One light plays sun then moon (fades out at mid-twilight while it swings). Cameras clear to Skybox.
  Dawn: the last `dawnFraction` of the night plays it backwards (slow `brightening` curve), sun rising at `dawnAzimuth`.
- **Retro look (optional, per player):** `RetroLook` ("Retro Look" scene object; LOOK button in the main/pause menu,
  saved in PlayerPrefs): low render scale + nearest-neighbour upscaling (chunky pixels, HUD stays sharp), the
  `RunForYourLife/RetroPost` full-screen pass on the URP renderers (colour steps + Bayer dither), and a priority-10
  Volume (more bloom/grain/vignette). It edits the URP asset's render scale at runtime and always restores it.
- **City:** built fresh every run. `CityManager` (on the GameFlow object) builds a lobby city in the menu; the host
  calls `PrepareHost()` before StartHost (new random seed, city, runtime NavMesh bake for Giant + Climber, monsters
  placed), the seed is synced and clients build the same city (`CityManager.Built` event for anything that caches map
  stuff). `CityGenerator` lays out the city from `CitySettings` (Assets/Settings); `BuildingBuilder` makes each
  building (house/shop/warehouse) with materials from `BuildingPalette`. All randomness = one seeded System.Random.
  Alleys must stay closed to the giant (radius 2.5): they're lined with walls whose gaps are 1.2 m. Runtime NavMesh
  is baked from physics colliders, so colliderless litter/windows don't affect it; keep anything big near a street
  curb collider-free or the giant can get stuck between it and a lamp.
- **Cars:** `Vehicle` prefabs parked by CityGenerator (stalls = small cars only; main-road curb + empty lots also pickups/trucks/ambulance), paint recoloured / wrecks rusted with shared material copies. CC-BY credits in `Docs/CREDITS.md` (must ship in the game). GLB models: convert to FBX with Blender 5.1 (installed) headless.
- **Night job (task 9, 15):** `GeneratorJob` (GameFlow object, host-owned): generator placed against the wall beside the
  start door, lamps within powerRadius go dark (StreetLamp.SetPowered) when fuel runs out (burns only at night).
  It also powers the van's beacon: while it's off the dawn countdown and sky stop (GameFlow.DawnOnHold). Starts with 30 s;
  cans = what the night needs + spareCans, and a can only pours if it all fits, so fuel can't be wasted;
  fuel cans at City.hidingSpots 40-140 m away; E pick up / help carry, G drop, hold E at the generator to pour.
  The can hangs from the LEFT hand bone (right = flashlight), placed in LateUpdate at execution order 200 so it swings
  with the arm. Solo = 0.75 speed, no sprint, slosh noise; pair = 0.9 speed (FPC.SpeedScale / CanSprint). E there wins over doors
  (DoorManager checks ClaimsUse). `CarAlarms` (GameFlow object): some parked cars ring when you walk into them.
- **World:** `BoxUV` gives scaled cubes real-world texture tiling. `AmbientAudio`, `WindowFlicker`, `LobbyCameraDrift`.

## Editor tools (Tools > RunForYourLife)
Building Generator (adds a roof hatch), Upgrade Existing Buildings, Add Roof Hatches To Existing Buildings, Widen Interior Doorways, Add Reach Points To Existing Buildings, Building Texturer (Use Texture
Set), Street Lamps, Monster Animator Builder, Climber Setup (clips, agent type, controller, scene object, NavMesh bake), Scout Setup (imports, controller, Scout prefab),
Make It Look Good, City Preview (generate a city from a seed next to the map), Retro Look Setup, Player Model Setup,
Vehicle Setup (car models in Assets/Models/Vehicle -> Assets/Prefabs/Vehicles, real size, URP materials, CitySettings.vehicles). Prefer improving these tools (and
CityGenerator) over hand-editing scenes; the scene has no buildings, the city is generated.

## Conventions and lessons learned
- Expose tuning values in the Inspector with tooltips. Comment scripts in plain language.
- Only the host runs MonsterAI; visual/audio effects run on each client from synced state.
- Giant NavMesh: agent type "Giant" (radius 2.5, height 14). Baked at runtime on the host (no baked data in the scene). NavMesh floats above
  the floor; MonsterAnimator snaps the model to the real floor.
- Mixamo clips: Rig = Humanoid; Loop Time on looping clips; Root Transform all Bake Into Pose, Position (Y) Based Upon
  Feet; rename clips from "mixamo.com" before using them. Exception: a clip downloaded without "In Place" (it walks
  off) must NOT bake XZ into the pose, or the body drifts and snaps back each loop. Use Root Rotation "Based
  Upon: Original" (Body Orientation skews strafes), then fix clips authored at an angle with the rotation offset.
- ProBuilder shapes: change Shape Size, keep Transform scale at 1.
- Git: always `git pull --no-edit` before `git push`. Binary assets (png/jpg/fbx/wav) go through Git LFS.

## Assets: what exists and what's still needed
Only ONE monster model exists: the giant (a Mixamo humanoid scaled 8x, Model child of the Monster object).
**When a task needs a model, animation or sound that isn't in the project, stop and tell Petar exactly what to get,
where from, and the import settings, before writing code that depends on it.**

| For | Needed | Where / what to pick | Notes |
| --- | --- | --- | --- |
| Climber (prototype step 4) | Lanky humanoid + climb, crawl, drop-down, idle | Mixamo: a thin character; anims "Climbing", "Crawl", "Jumping Down", "Zombie Idle" | Humanoid rig, ~3-4 m tall (scale ~2). Same import settings as the giant. |
| Scouts (task 6b/6c) | Tiny humanoid (goblin/imp) + crawl, idle/sniff, climb, screech, death; screech + death + skitter sounds | Mixamo or Quaternius (CC0) model; Mixamo "Crawl"/"Running Crawl", "Crawl Idle", "Climbing", "Zombie Scream", "Zombie Dying" (In Place); sounds from freesound.org (CC0) | Humanoid rig, scaled to ~0.6 m, same import settings as the giant. Eyes/ears as separate meshes or materials if possible. Assets/Monsters/Scout/. |
| Giant low peek | Prone / crawl pose | Mixamo "prone" or "crawl", frozen to 1-2 frames (Start/End in the clip settings) | Goes in the Peek state. |
| Near miss / captured | Ground slam, struggle, scream sounds | freesound.org (CC0 filter) | Force To Mono for 3D sounds. |
| Night job | Generator loop, refuel sounds; a generator model | freesound.org; Kenney or Quaternius props (CC0) | |
| Burrower, swarm, phaser | Later, after the prototype | | Don't build these yet (see Docs/DESIGN.md, Scope). |
