# Run For Your Life: Game Design (summary)

The living version is the shared design doc; this file is the copy Claude Code reads.

## Pitch
Friends get one evening to fortify a building before something arrives, and nobody knows what it is. Each monster is
built from hidden traits; monsters that kill your group pass their traits on, so over many runs it evolves to beat how
your group plays.
Pillars: prepare for a guess · it adapts to you · learn it in the dark · co-op hero moments.

## Core loop (one night per run)
Monster drawn (hidden traits, weighted by the group's lineage) -> Prep, 10-15 min (scavenge, read clues, watch scouts,
fortify) -> Night (deduce what it senses, survive until dawn) -> Lineage evolves (killer traits spread, beaten traits
fade) -> next run. Win = survive until dawn (the only win). Lose = every player dead.

## Decisions
- One night per run; ends at dawn or when every player is dead.
- Grabbed players aren't dead: the monster hides them in the city. Clues: drag marks and their voice calling out
  (a Mimic can fake it). A hidden player dies 8 minutes after capture and stays dead until the next run (next day). Teammates who free them revive them.
  The monster guards, baits with, or ignores the spot depending on its behavior trait.
- A weakness only repels the monster for a while; dawn is still the only way out.
- Lineage saved per friend group on the host's PC.
- The Mimic copies real recorded voice chat; every player opts in.
- A new group's first monster is a small random mix of genomes.

## Monster genome
| Category | Options | Effect |
| --- | --- | --- |
| Sense | sight, hearing, smell, vibration, body heat | what gives you away |

Each monster type is born with a set of senses (GenomeManager.startingSenses): the giant sees and hears (the genome
makes one strong), the Climber is blind and hunts by sound (it only feels you right next to it; its scouts are its
eyes). A sense a type lacks is never picked for it. Later, evolution can grant a lineage a missing sense as a visible
mutation (a Climber line that keeps winning grows eyes, and its scouts show it).
| Access | giant reacher, climber (roof), burrower (floor), swarm (cracks), phaser (through boards) | which defenses matter |
| Behavior | stalker, ambusher, hunter, mimic | how it hunts; hunter guards hidden players, ambusher baits, stalker watches |
| Weakness | light, loud noise, water, fire, a frequency | repels it for a while |

## Evolution
Winning monsters' traits gain weight, beaten ones lose it (never to zero). Counter-evolution: traits that beat the
group's usual defenses get boosted. Visible mutations as a lineage specializes. Quits/disconnects count as monster wins;
trait weights are capped; a random mutation floor keeps surprises.

## Scouts first
Small creatures at dusk share the monster's genome; how they behave (and what a dead one has: eyes, ears) reveals traits.
Where they gather marks where the monster will try first.
Scouts are tiny (~0.5-0.7 m), crawl the streets and climb walls, and get indoors only through broken or partly
broken windows (some buildings start with a small hole in the glass: a scout fits, a person doesn't). They are the
monster's eyes: a scout that spots a player runs off and screeches (2-3 s wind-up, audible); if it finishes, the
monster learns where that player WAS (stale info). Kill it before the screech ends and nothing is passed on. Seen,
lit or chased scouts flee. Killing one = flashlight + stomp: holding the beam on a scout freezes it for a moment
(it's dazzled), and a player who reaches it stomps it. Two players work best (one lights, one stomps). Sight scouts flee flashlights, Hearing scouts home in on noise.

## The Director (Alien Isolation style, but with a leash you can cut)
Host-only. Always knows where every player is and keeps a tension meter. It never tells the monster directly: it
sends scouts toward the players' general area, more often the longer things stay calm. Info reaches the monster only
through scouts, noise and its own senses. With no scouts left, the Director falls back to vague, slow hints (the
monster drifts toward your part of the city) so players are never fully invisible.

## The Mimic (rare evolved behavior)
Stage 1 copies footsteps and flashlight clicks; stage 2 copies movement; stage 3 plays back recorded voice snippets.
Counterplay: code words, never open the door to a voice alone.

## Clues
Claw marks high on walls (climber), drag marks/soil (burrower), carcass with eyes untouched (not sight), torn boarded
window (reacher), garbled radio broadcasts, scout behavior. Bestiary + lineage tree fill in over runs.

## Base building
Barricades (vs reacher; useless vs burrower/phaser) · lure traps (vs hearing) · light towers (vs light-weak; attract
light hunters) · panic room · decoy room (heat/noise) · reinforced floor (vs burrower).

## Bunker hub (after the prototype)
**Scope:** nothing here until the prototype is finished and playtested (task 8, generator, playtest first).
- **First version (small):** one bunker room, a radio that names tonight's town and job, 3-4 upgrades that visibly
  change the room, a corkboard bestiary. Salvage brought back at dawn is the only currency. Night jobs come with it.
- **Later:** more rooms, the previous group's notes/tapes, bunker damage on a loss, the map table, the van as a
  physical place (load gear at dusk, reach it at dawn).

The group lives in a worn-down bunker between nights, R.E.P.O.-style: the bunker is like its Service Station
(spend what you earned), and the van is like its truck (it takes you to the level and is where you extract).
- **Loop:** bunker (spend, plan, read the radio) -> the van drives you to tonight's town and leaves -> prep +
  night in a building you fortify (the core loop is unchanged; you never spend the night in the bunker) -> dawn: the
  van comes back to a pickup point; whoever reaches it brings back what they carry.
- **Why a new layout every run:** it's a different town each night. The region is evacuated and the towns near the
  bunker are picked clean, so every supply run goes further out, to somewhere new.
- **Why you're there at night:** the drive takes most of the day, so you arrive at dusk (the prep phase). The van
  can't drive back in the dark (headlights draw it, roads are blocked) and hides until dawn, so you're stuck there
  for the night. Dawn = the van returns = the win.
- **Night jobs:** optional goals that pay salvage and make you leave your fortified building (also the fix for
  camping): bring back N scout bodies, record the monster's call near its patrol, recover a supply crate, refuel a
  generator, rescue a teammate. Surviving until dawn is still the only win; jobs only decide how much you earn.
  Exception (2026-10-30): the generator powers the van's beacon, so dawn doesn't come while it's off. Refuelling is
  required, and that's what gets players outside.
- **Losing:** salvage you carry is lost if you don't reach the van at dawn. A lost night can also break something in
  the bunker (a light goes dark, a room floods) so losses feel real without wiping progress.
- **Upgrades are options, not power** (they must not fight the monster's evolution, and every one gets a counter):
  workshop (better/faster barricades, lure traps), radio room (clearer broadcasts, clues lie less), infirmary
  (a captured player lasts longer), map table (see part of tonight's layout, pick the drop-off area), locker
  (second flashlight, flares). Bought gear is loaded into the van at dusk.
- **Make it feel good:** every upgrade shows: rooms light up one by one, the generator starts humming, collapsed
  doorways get cleared into new rooms. Trophies: scout skulls on a shelf, claw marks traced on a wall chart, the
  lineage tree and bestiary pinned to a corkboard that fills in over runs.

## Story (draft)
The bunker was a civil-defence outpost, and the group that lived there before you lost. As you repair rooms you
find their notes, tapes and radio logs: they fought the same monster lineage and describe how it learned *them*.
The radio room picks up broadcasts from whoever is still out there (the "garbled radio" clue). Questions to answer
over many runs: where the monsters come from, what happened to the region, and why this lineage keeps adapting to
your group. Possible late twist: the Mimic plays back the previous group's voices.

## Chases and catches
Stare (silence, ~1 s) -> scream and sprint, far faster than players in the open -> escape is geometry (it turns wide,
can't fit alleys, slows at doorways) -> near miss slams the ground -> catch: hand fills the screen, sound drops out.
Player feel: tunnel vision, muffled hearing except its footsteps, racing heartbeat, building shake, look-back key.

## Losing a teammate
Every capture is a crisis: the team gets weaker and the monster stronger. Build 1 and 2 first.
1. Two-person jobs (carry fuel, hold a door while boarding, lift heavy barricades); solo versions are slow/risky or impossible.
2. It feeds: each capture makes the monster faster, sharper and more aggressive for the rest of the night.
3. Safety in numbers: alone, heartbeat louder, screen edges darker, sounds play tricks.
4. Their gear goes with them (radio, toolkit, generator key) until they're rescued.
5. The Mimic gets the captured player's voice.

## Risks and fixes
- Camping until dawn (Prototype): night jobs outside (refuel generator, rescue, recover supplies), barricades wear down,
  lineages drift toward climbers/burrowers.
- Boring capture (Prototype): struggle to slow the timer (makes noise), call out, glimpses through the monster's eyes,   roll around if possible.
- Steering evolution (Prototype): quits = monster wins, capped weights, mutation floor.
- Solved meta (Later): sometimes-unreliable clues, trait strengths vary, every defense has a counter.
- Lure spam (Later): it habituates to repeated lures.
- Repetitive prep (Later): randomize buildings/supplies/weather; aim for 6-8 min prep.
- Dead time (Later): AI director for buildup / scare / breather.
- Mimic beaten by a code word (Later): extra tricks.
- Scope (Always): finish the prototype, playtest, let fun decide.

## First prototype (build in this order)
1. Genome data: Sense (sight/hearing) x Access (reacher/climber) = 4 monsters, applied to MonsterAI at run start.
2. Prep timer and night timer.
3. Barricades on doors/windows.
4. Climber access (over the roof, in through a hatch).
5. Three clues.
6. Scouts sharing the genome.
7. Evolution save file (JSON on the host).
Plus: generator refuelling at night, struggling while captured, quits count as monster wins.
