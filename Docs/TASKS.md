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
- [ ] **4.5 Bigger random city (L, Opus).** ~3x the current map, regenerated every run, more variety. Buildings stay
  single-storey (4-7 m tall); real 2-storey waits until after the prototype playtest. Split:
  [x] a) runtime `BuildingBuilder` (Scripts): the box/door/interior/reach point/hatch/details/texture code moves out of
     the editor tools so the game can build buildings; editor tools call it too. Size + height variants (house, shop,
     warehouse). No behaviour change for existing buildings;
  [ ] b) `CityGenerator` (Scripts): seed -> irregular blocks, a wide main road, alleys too narrow for the giant
     (radius 2.5), plaza, park, parking lot, empty lots; street lamps, PlayerSpawn, PatrolCenter. Tools menu preview;
  [ ] c) in-game: host picks the seed (synced), every client builds the same city (System.Random, not
     UnityEngine.Random), runtime NavMesh bake for Giant + Climber behind a loading fade, MonsterAI/ClimberAI refresh
     their roof/reach caches, monster spawn points;
  [ ] d) tuning: prep ~8 min (DESIGN: 6-8), patrol area, light count/performance, playtest.
- [ ] **5. Clues (S, Sonnet).** Three clue prefabs spawned at prep start based on the genome: claw marks high on a wall
  (Climber), a carcass with eyes untouched (not Sight), one radio line (random). Simple decals/props are fine.
- [ ] **6. Scouts (L, Opus).** Needs a scout model first. Split: 6a) small scout AI (wander, flee light or noise
  depending on genome, attack weakly); 6b) spawn 3-5 at dusk, sharing the genome's sense.
- [ ] **7. Evolution save (S, Sonnet).** Host saves trait weights to JSON in Application.persistentDataPath after each
  run; winning monster's traits +weight, beaten -weight, min floor and max cap (Inspector). Quitting/disconnecting
  counts as a monster win. GenomeManager uses the weights.
- [ ] **8. Captured, hidden, rescued (L, Opus).** Split: 8a) on catch, the giant carries the player to a random hiding
  spot (empty markers placed in the scene), 5-min death timer synced to everyone (Inspector value; after it runs out they are dead until the next run); 8b) drag-mark decals from catch to
  spot + the captured player's voice is their clue; teammates hold E to free them (revive); 8c) struggle key slows the
  timer but emits noise. Replaces the current fade-to-black respawn.
- [ ] **9. Generator night job (M, Sonnet).** Generator powers nearby street lamps/lights; fuel runs out every few
  minutes; fuel cans spawn outside; carrying one is a two-person job (two players hold it, or one player at half speed
  making noise). Needs a generator model + sounds first.
- [ ] **10. It feeds (S, Sonnet).** Each capture: monster +10% speed, +10% sight/hearing range, shorter stare
  (Inspector values), reset at run start. File: MonsterAI.cs.
- [ ] **11. Chase feel (M, Sonnet).** Open-ground sprint (no building within ~8 m -> openGroundSpeed 16), near-miss
  ground slam, tunnel vision + muffled audio as it closes in, look-back key (Q), silence on the stare.
  Files: MonsterAI.cs, MonsterAudio.cs, CameraShake.cs, FirstPersonController.cs.

## After the prototype
Playtest with friends before building more. Then pick from Docs/DESIGN.md: Mimic, bestiary, mutations, AI director,
more access types.
