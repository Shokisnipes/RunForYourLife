# Task list (Claude Code + Unity MCP)

How to use: start Claude Code, `/clear`, then say **"Do task N from Docs/TASKS.md"**. One task per session.
When it's done and tested, Claude Code ticks the box, commits and pushes.

## Token rules (paste-free: Claude Code, follow these on every task)
- Read only the files the task names. Use Grep to find things instead of opening whole folders.
- Don't re-read a file you just edited. Don't take scene/camera captures unless the task says to unless absolutely neccessary; check the Unity console for errors instead. At most 3 captures per task.
- Make the change, play-test once, fix errors, then report in 3-5 lines. No long explanations.
- If a model/animation/sound is missing, stop and tell Petar what to get (see CLAUDE.md Assets) - don't build around it.
- Stop and ask before anything not in the task.

## Tips for Petar
- Model: **Sonnet** unless the task says Opus. `/model` to switch.
- `/clear` between tasks. If a session passes ~100k tokens (`/context`), finish up and `/clear`.
- In Unity's MCP settings, keep only the tools you use enabled: every enabled tool costs tokens on every message.
- Most tokens go to screenshots and playtesting loops. Do the playtesting yourself and describe what you see in
  one or two sentences.

## Prototype tasks (in order)
Sizes: S = ~20-40k tokens, M = ~40-80k, L = split it if it runs long.

- [x] **1. Monster genome (S, Sonnet).** MonsterGenome ScriptableObject: Sense (Sight/Hearing) and Access
  (Reacher/Climber, Climber stubbed). Sight = high sightRange, low hearingMultiplier; Hearing = the opposite.
  GenomeManager on the NetworkManager object: host picks a random genome at game start and applies it to MonsterAI.
  Log the pick. Files: MonsterAI.cs, NetworkUI.cs.
- [x] **2. Prep and night timers (S, Sonnet).** GameFlow (host-run, synced): Prep phase (Inspector length, default
  6 min, monster disabled) then Night until dawn (default 10 min). Win screen at dawn; lose screen when every player is
  dead. Small top-of-screen timer. Files: NetworkUI.cs, MonsterAI.cs, FirstPersonController.cs.
- [x] **3. Barricades (M, Sonnet).** Players hold E at a doorway/window to board it during prep (limited planks,
  Inspector value). A boarded doorway disables its ReachPoint so the giant can't reach in. Boards are networked
  objects. Files: ReachPoint.cs, MonsterAI.cs (FindReachPoint).
- [x] **4. Climber access (L, Opus).** Needs the climber model first (CLAUDE.md Assets). Split:
  [x] 4a) climber moves on streets + climbs a building wall to the roof (scripted climb; ClimberAI, Climber Setup tool);
  [x] 4b) roof hatch: drops inside through a hatch the building generator adds. Barricades don't stop it.
- [x] **4.5 Bigger random city (L, Opus).** ~3x the current map, regenerated every run, more variety. Buildings stay
  single-storey (4-7 m tall); real 2-storey waits until after the prototype playtest. Split:
  [x] a) runtime `BuildingBuilder` (Scripts): the box/door/interior/reach point/hatch/details/texture code moves out of
     the editor tools so the game can build buildings; editor tools call it too. Size + height variants (house, shop,
     warehouse). No behaviour change for existing buildings;
  [x] b) `CityGenerator` (Scripts): seed -> irregular blocks, a wide main road, alleys too narrow for the giant
     (radius 2.5), plaza, park, parking lot, empty lots; street lamps, PlayerSpawn, PatrolCenter. Tools menu preview;
  [x] c) in-game: host picks the seed (synced), every client builds the same city (System.Random, not
     UnityEngine.Random), runtime NavMesh bake for Giant + Climber behind a loading fade, MonsterAI/ClimberAI refresh
     their roof/reach caches, monster spawn points;
  [x] d) tuning: prep ~8 min (DESIGN: 6-8), patrol area, light count/performance, playtest.
- [x] **4.6 Dusk to night sky.** Prep starts as a fall evening (~8 pm) and fades to night as the timer runs down;
  moon that lights the scene, clouds day and night (SkyCycle + Sky shader).
- [x] **4.7 Deserted city dressing.** Interior layouts (open / back room / side room / two back rooms) + furniture;
  road paint, crosswalks, cracks, puddles, litter, wrecks on the main road; walls along alleys (narrow gaps) so the
  giant can never get in; NavMesh baked from colliders; night ambience fades in with the dark.
  [x] dusk ambience clip (Calm_Day_Sound, AmbientAudio.duskLoop) crossfades out as night falls.
- [ ] **4.8 Player body (L, split).** Head bob toned down. Martha (Mixamo, Assets/Characters/Martha) replaces the
  capsule; Tools > RunForYourLife > Player Model Setup does imports, the controller and the prefab.
  - [x] a. Imports, strafe/crouch blend trees with stride matching, own body visible (head hidden), others' heads
    follow their look. Host-tested (forward + strafe drive the right clips, no errors). Petar to play-test the feel.
  - [x] c. (REMOVED 2026-10-27, it fought the view behind short walls) Cover: crouch next to a wall = back to the wall + cover sneak (PlayerCover); view eases off the wall
    (mouse free); at a wall's end you stop and lean out to peek. Synced. Running strafes, crouch strafes and
    crouch-back clips added. Small mouse smoothing (lookSmoothing 0.02 s). Host-tested with simulated keys.
  - [ ] b. Flashlight in the right hand (arm IK toward where you look), turn-in-place (TurnLeft/Right and
    CrouchTurnLeft/Right are imported, not wired), 2-player check of other players' bodies (peeking was removed with cover on 2026-10-27).
    Done and host-tested: FlashlightHand (simple-shapes flashlight in the fist, arm up when on, beam from the lens)
    and turn-in-place in PlayerAnimator (body holds until 60 deg, then steps round with the turn clips).
    Left: Petar's 2-player check.
- [x] **5. Clues (S, Sonnet).** Three clue prefabs spawned at prep start based on the genome: claw marks high on a wall
  (Climber), a carcass with eyes untouched (not Sight), one radio line (random). Simple decals/props are fine.
  Done: ClueManager (GameFlow object). Wall: claw gouges (Climber) or a torn-off door + giant handprint (giant);
  dog carcass, eyes gone = Sight; radio with generated static + garbled subtitles about the sense or the access.
  Placed 18-70 m from spawn from the city seed; each clue has an Inspector chance to lie. Host-tested both monsters.
- [x] **6. Scouts + Director (L, Opus, split).** Host-tested 2026-10-28: spawn, Director sends scouts, spot ->
  screech -> relay (giant goes to Investigate the spot), flee away from players, beam freeze, stomp kill.
  6a done: ~15% of windows start Holed (scout-sized hole, people blocked); scouts route through holed/broken windows
  when a door is shut (scripted hop). 6b done: scouts climb walls onto flat roofs (to escape players or for a
  look around) and climb back down at the edge nearest where they're going. 6c done: Sight scouts have big
  glinting eyes, Hearing scouts big ears + blind eyes (synced, kept on the body). 2-player join test passed
  (client got all scouts, same city, no errors). Petar to play-test the feel. See DESIGN "Scouts first" and "The Director".
  - [x] a. Director (host): tracks players, tension meter, picks areas to send scouts, fallback vague hints to the
    monster when no scouts are alive. Partly broken windows: new BreakableWindow state (small hole, scout-only),
    some buildings start with them; scouts also use fully broken windows. No assets needed.
  - [x] b. Scout movement: tiny model (~0.6 m), NavMesh agent type "Scout", crawls streets, scripted wall climbs +
    raycast surface sticking, enters through broken/holed windows. 3-5 spawn at dusk, share the genome.
    Needs the scout model + clips (CLAUDE.md Assets).
  - [x] c. Relay, flee, kill: spot a player -> run + screech wind-up -> MonsterAI gets the stale position (new
    Investigate source). Flee when seen/lit/chased (Sight: flashlight, Hearing: noise). Players can kill it
    by flashlight + stomp: the beam freezes it briefly, then stomp it; dead ones show eyes/ears.
  Body + sounds DONE (Tools > Scout Setup -> Assets/Prefabs/Scout.prefab, ScoutAnimator, ScoutAudio, levels in AudioMix).
- [x] **7. Evolution save (S, Sonnet).** Host saves trait weights to JSON in Application.persistentDataPath after each
  run; winning monster's traits +weight, beaten -weight, min floor and max cap (Inspector). Quitting/disconnecting
  counts as a monster win. GenomeManager uses the weights.
  Done: GenomeManager "Evolution" settings (winBoost 0.5, lossPenalty 0.3, min 0.25, max 4, mutationChance 0.15),
  evolution.json (Sense.X / Access.X weights; genome weight = sense x access). Host leaving or quitting before dawn
  or a wipe = monster win. Host-tested: dawn -> beaten, quit mid-run -> won, file written.
- [x] **8. Captured, hidden, rescued (L, Opus).** Split: 8a) on catch, the giant carries the player to a random hiding
  spot (empty markers placed in the scene), 5-min death timer synced to everyone (Inspector value; after it runs out they are dead until the next run); 8b) drag-mark decals from catch to
  spot + the captured player's voice is their clue; teammates hold E to free them (revive); 8c) struggle key slows the
  timer but emits noise. Replaces the current fade-to-black respawn.
  - [x] a. Done + host-tested 2026-10-28: CityGenerator lays HidingSpot markers (park, parking aisles, empty lots);
    MonsterAI Carry state walks you 35+ m away from free players, stoops and puts you down; you lie there (look
    only) with a synced timer on everyone's HUD (FirstPersonController.hiddenSurviveSeconds 300), then die. Climber
    leaves you hidden where it caught you. Run is lost when nobody is free (all dead or hidden).
    2-player check passed (Multiplayer Play Mode): the client sees the carry, the stoop and the body where it's put down.
  - [x] b. Done 2026-10-28: PlayerRescue on the Player prefab: blood drips under a carried player every 2.5 m (every
    client) + a pool at the hiding spot; hold E 4 s within 2.5 m of a hidden teammate (noise every second) -> host
    checks -> FirstPersonController.Free() (timer stops, ragdoll stands up). Voice clue = live proximity voice chat
    (VoiceChat + VoicePlayback, Opus via Concentus; MIC button in the menus picks the device, M mutes).
  - [x] c. Done 2026-10-28: tap Space while hidden = timer at struggleTimerSpeed 0.5 (host-timed, can't beat it by
    mashing), noise 10 m at most 1/s, ragdoll jolts on every client (PlayerRagdoll.Wiggle), small camera jolt.
- [x] **9. Generator night job (M, Sonnet).** Generator powers nearby street lamps/lights; fuel runs out every few
  minutes; fuel cans spawn outside; carrying one is a two-person job (two players hold it, or one player at half speed
  making noise). Needs a generator model + sounds first.
  Done 2026-10-28, host-tested (stall -> circuit lamps off, pick up, solo slosh noise, hold E refuel -> restart):
  GeneratorJob on GameFlow. Left: Petar's 2-player check of carrying together. Slosh = 3D loop on the can (Gas_Slosh, -27 dB).
  - [x] b. Car alarms (Petar's idea): CarAlarms on GameFlow, ~35% of parked non-wreck cars; walking/running into
    one rings it 20 s (orange blinking light, noise 70 m); host-tested (giant went to Investigate).
  Also real car models replace the box cars (Tools > Vehicle Setup, Docs/CREDITS.md).
- [x] **10. It feeds (S, Sonnet).** Each capture: monster +10% speed, +10% sight/hearing range, shorter stare
  (Inspector values), reset at run start. File: MonsterAI.cs.
  Done 2026-10-29, host-tested: MonsterAI "It feeds" header (speed/sense +10% per catch, stare -15% per catch, min
  0.5 s, max 5 catches; carry speed unchanged). Reset in ApplyGenome. Climber feeds too (speed + sight/hearing/feel,
  no stare); same code as the giant, compiled, not host-tested (play mode kept stopping).
- [x] **11. Chase feel (M, Sonnet).** Open-ground sprint (no building within ~8 m -> openGroundSpeed 16), near-miss
  ground slam, tunnel vision + muffled audio as it closes in, look-back key (Q), silence on the stare.
  Files: MonsterAI.cs, MonsterAudio.cs, CameraShake.cs, FirstPersonController.cs.
  Done 2026-10-29, Petar checked: open-ground sprint, Q look-back, stare silence (heartbeat + music cut). Near miss =
  1.2 s stop with the hand on the street (MonsterAnimator.StartSlam squat) + GroundSlamFx crater (dust, grit, chunks,
  slab rim, generated crack decal: Tools > Crack Decal Textures / Ground Slam FX Setup). Slam sound = footstep
  stand-in until a real clip. Tunnel vision/muffling already existed (ChaseStress).
- [ ] **12. Giant reaches into alleys (L, Opus).** Today it sees you in an alley it can't fit into and just runs
  around. Instead: it stands at the alley mouth (or beside the alley over the buildings), bends over to suit what's
  around it (lower over low walls, round corners) and reaches in with one arm. If a wall, house or big object is in the
  way, it braces its other hand on it (hand planted on the roof edge / wall top) while it leans in. Builds on the
  doorway Reach (ReachPoint, arm IK, hand feeling around) and the put-down stoop/bow in MonsterAnimator.
  Petar's idea, 2026-10-28; do after task 8.

- [ ] **13. City fill (L, split, one sub-task per session).** Petar, 2026-10-29: the streets, lots, parks and open
  areas don't have enough stuff or buildings. Everything is built in code from simple shapes (CityGenerator /
  BuildingBuilder), usable as is or as placeholders: each prop gets its own small function so a model can replace it
  later. Rules: seeded rng only; nothing big with a collider near a curb (the giant gets stuck); alleys stay closed to
  the giant (gaps <= 1.2 m); small clutter has no collider; keep an eye on draw calls (Optimize).
  - [x] a. Street furniture (Sonnet, done 2026-10-29, Scripts/CityStreetFurniture.cs): utility poles with sagging power lines down one side of each street, traffic
    lights / stop signs at junctions, street name signs, hydrants, mailboxes, newspaper boxes, trash cans, bus stops
    with shelters, bollards, parking meters, manhole covers, cones and road barriers. New CitySettings densities.
  - [ ] b. Fuller blocks (Opus): buildings on the short ends and corners of blocks (corner shops), tighter rows
    (fewer and smaller gaps), garages and sheds; backyards behind rows get fences, sheds, clotheslines, grills, kiddie
    pools, junk. More buildings per city overall.
  - [ ] c. Parks, plaza and lots (Sonnet): parks get a playground, gazebo, pond, hedges, picnic tables, statue, more
    trees and bushes; the plaza gets kiosks, market stalls, planters, a phone booth, a bus shelter; empty blocks become
    a construction site or junkyard (containers, scaffolding, portable toilets, sand/gravel piles, pallets); parking
    lots get a booth, light poles, cart corrals.
  - [ ] d. Abandonment (Sonnet): what happened here. Police roadblocks with barriers and police cars, an evacuation
    camp (tents, cots, crates, sandbags) in a park or the plaza, dropped suitcases and bags, burnt-out cars, spray-
    painted warnings and missing posters on walls, boarded windows, blood trails. Ties into Clues / Story (DESIGN.md).
  - [ ] e. New block types (Opus): gas station (fuel cans can spawn there), church, school with a fenced field,
    small store with its own parking; 1-2 per city, picked by the seed. Placeholders now, models later.
  - [ ] f. Swap in models (when Petar has them): Kenney City Kits / Quaternius (CC0), see CLAUDE.md Assets.

- [ ] **14. Living world (M, split).** Petar, 2026-10-29: make the game feel more alive. Today: dusk birds bed, a very
  quiet night drone (AmbientAudio.ambienceVolume 0.067 in the scene), flickering lamps/windows, clouds, car alarms.
  AmbientAudio.distantSounds is EMPTY, so the random distant one-shots never play.
  Suggested order: b, a, c, then d, e.
  - [ ] a. Wind (code): gusts sway trees and power lines, paper/litter blowing down streets, swinging shop signs, some
    doors/shutters banging in abandoned buildings, flapping tarps. Needs wind gust + creak/bang sounds.
  - [ ] b. Sound layer (needs sounds first): fill distantSounds (dog barks, far siren, metal clangs, glass breaking);
    3D emitters: buzzing lamps, humming transformers on the 13a poles, dripping pipes, wind whistling through alley
    gaps, a radio/TV muttering in some houses.
  - [ ] c. Creatures: crows on wires/roofs that burst into the air when you get close (a noise the monster hears;
    birds lifting far away hint where the giant is), rats along alley walls, a stray dog glimpsed far off.
  - [ ] d. Signs of life: TVs flickering blue in some windows, a sparking power line, a car with hazards blinking,
    a payphone that rings (story hook, DESIGN.md Clues/Story).
  - [ ] e. Weather: rainy nights (rain, wet streets, sound), lightning that shows the giant's silhouette, fog banks.
  Sounds Petar needs (freesound.org CC0, 3D = Force To Mono): distant dog barks, far siren, metal clangs, glass
  breaking, crow caws + wing flaps, electrical hum/buzz, dripping water loop, wind gusts, door creak/bang, TV static,
  an old public-domain radio song. Rain loop + thunder for e.

- [ ] **15. Something to do at night (Petar, 2026-10-30).** Generator starts with 30 s; 4 cans for a 10 min night + 2
  spare; no power = dawn on hold (HUD warning, generator fuel on the HUD); a can only pours if it all fits. Giant and
  Climber hunt the same night (GenomeManager.bothMonsters), scouts on, prep 3 min, night 10 min. Climber catch = dead
  on the spot, no timer, revivable (FPC.Down). Host-tested: 6 cans, both hunting, dawn holds and resumes, real Climber
  catch -> downed -> run lost when alone, Free() stands you up. Left: Petar's 2-player check of a hold-E revive,
  pouring cans by hand and how the fuel runs feel.

## After the prototype
Playtest with friends before building more. Then pick from Docs/DESIGN.md: Mimic, bestiary, mutations, AI director,
more access types.
