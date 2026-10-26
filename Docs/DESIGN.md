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
  (a Mimic can fake it). A hidden player dies for good 10 minutes after capture. Teammates who free them revive them.
  The monster guards, baits with, or ignores the spot depending on its behavior trait.
- A weakness only repels the monster for a while; dawn is still the only way out.
- Lineage saved per friend group on the host's PC.
- The Mimic copies real recorded voice chat; every player opts in.
- A new group's first monster is a small random mix of genomes.

## Monster genome
| Category | Options | Effect |
| --- | --- | --- |
| Sense | sight, hearing, smell, vibration, body heat | what gives you away |
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

## The Mimic (rare evolved behavior)
Stage 1 copies footsteps and flashlight clicks; stage 2 copies movement; stage 3 plays back recorded voice snippets.
Counterplay: code words, never open the door to a voice alone.

## Clues
Claw marks high on walls (climber), drag marks/soil (burrower), carcass with eyes untouched (not sight), torn boarded
window (reacher), garbled radio broadcasts, scout behavior. Bestiary + lineage tree fill in over runs.

## Base building
Barricades (vs reacher; useless vs burrower/phaser) · lure traps (vs hearing) · light towers (vs light-weak; attract
light hunters) · panic room · decoy room (heat/noise) · reinforced floor (vs burrower).

## Chases and catches
Stare (silence, ~1 s) -> scream and sprint, far faster than players in the open -> escape is geometry (it turns wide,
can't fit alleys, slows at doorways) -> near miss slams the ground -> catch: hand fills the screen, sound drops out.
Player feel: tunnel vision, muffled hearing except its footsteps, racing heartbeat, building shake, look-back key.

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
