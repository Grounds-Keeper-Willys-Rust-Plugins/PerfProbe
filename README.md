# PerfProbe

A Carbon/Oxide plugin for finding out why a Rust server lags.

It runs inside the server and watches every frame. Since 0.4.0 it also reaches the part of the frame that
has no name in any plugin: Unity's own player-loop systems, every script `Update`, and the components
that feed a busy engine system. When a frame is slow (a "spike"), it
records what was running in that frame: game methods, plugin hooks, plugin timers, garbage
collection, and which entities were spawned or killed. It also keeps a per-second timeline
of where the server's time went, and can time individual plugins, game methods and entity
types, so you can see what each one costs.

Everything is read through console commands (server console, F1 console as admin, or RCON).

## Install

1. Copy `PerfProbe.cs` into the server's `plugins` folder (`carbon/plugins` or `oxide/plugins`).
2. It loads straight away. The console shows what it is watching, for example
   `Watching 32 method(s)...`, `Timing plugin hooks for 33 plugin(s)` and
   `Timing entity create/spawn/kill per prefab via 4 method(s)`.
3. Let it run for a while (at least 15–30 minutes, longer for rare spikes), then use the
   commands below.

Requires Harmony, which Carbon and Oxide both include.

## Commands

All commands work from the server console and RCON. In the F1 console they need admin.

| Command | What it shows |
|---|---|
| `perfprobe.status` | Is it running, frames and spikes since the last reset, average fps and worst frame over the last 60 seconds, its own overhead, heap size, timeline state. Start here. |
| `perfprobe.spikes [count]` | The last spike frames (default 10, max 50), one line each: frame time, GC, the 8 most expensive things in that frame, entities spawned and killed, and per-prefab entity time. |
| `perfprobe.top [count]` | Totals since the last reset: what took the most time inside spike frames, what costs the most per minute overall, and which entities were spawned during spikes. |
| `perfprobe.spawns [count]` | Entity churn: which prefabs are spawned and killed, per minute. |
| `perfprobe.spawncost [count] [name]` | What each prefab costs to create, spawn and kill, in ms per minute and per call. Add part of a name to filter, e.g. `perfprobe.spawncost peacekeeper`. |
| `perfprobe.loop [seconds] [top]` | Times every Unity player-loop system (physics, navmesh, animation constraints, the script phases...) and prints ms per second and the worst single call for each, with a hint on what to run next for anything busy. With no arguments: the last snapshot (one is taken automatically every `LoopSnapshotMinutes`). **Run this first on a new map or host.** |
| `perfprobe.behaviours [seconds] [top] [filter]` | Stopwatches every `Update`, `LateUpdate` and `FixedUpdate` declared by any MonoBehaviour (game, framework, plugins) and prints the busiest. Names the script behind a busy script phase. `filter` limits it to type names containing the text. |
| `perfprobe.components <Type> [Type...]` | Counts live Unity components of a type (e.g. `NavMeshObstacle`, `RotationConstraint`): total, enabled, on active objects, carving, and the prefabs and objects that carry them, with a `[collider]` flag. Turns "this engine system is busy" into "these prefabs feed it". 1-40 ms per type on a big map. |
| `perfprobe.entities [count]` | How many entities of each prefab exist now, player-owned and not. **Loops over every entity in one frame (100–150ms on a big map): avoid while players are on.** |
| `perfprobe.watch Type.Method` | Start timing a game method, e.g. `perfprobe.watch BaseOven.Cook`. `Type.*` times all methods of a type. |
| `perfprobe.unwatch Type.Method` | Stop timing it. `Type.*` or `all` also work. |
| `perfprobe.find Type [filter]` | List a type's methods, to find the right name for `watch`, e.g. `perfprobe.find SpawnGroup Spawn`. |
| `perfprobe.patches` | List Harmony patches other plugins have put on game methods. |
| `perfprobe.timers [fix\|fixall]` | Find plugin timers that have silently stopped (see below). `fix` restarts lost repeating timers; `fixall` also restarts one-shot timers, which will then fire late. |
| `perfprobe.threshold <ms>` | Change what counts as a spike (default 50ms, minimum 5ms). Saved to the config. |
| `perfprobe.timeline <from unix time> [rows]` | Per-second timeline as JSON, for graphing. `0` means from the start. |
| `perfprobe.players <from unix time> [rows]` | Player position and activity samples as JSON, for graphing. |
| `perfprobe.tp <player> <x> <y> <z>` | Teleport a player from the server console, e.g. to stand somewhere while you measure it. |
| `perfprobe.reset` | Clear the spike, totals, churn and entity-cost stats. The timeline is kept. |

## Reading the numbers

- **Spike**: a frame that took longer than the threshold (50ms by default). At 60+ fps a normal
  frame is 10–16ms.
- **Inclusive time** includes everything the call ran, including other watched calls inside it.
  In `perfprobe.top` and `perfprobe.spikes`, entries can overlap: `ServerMgr.Update` contains
  almost everything else.
- **Self time** excludes the watched calls (or nested entities) inside it, so self times can be
  added up without counting anything twice. The timeline uses self time.
- **Entity costs** (`spawncost`) have three phases: `create` (building the object), `spawn`
  (starting it up, which for an NPC includes creating its gear) and `kill`. "including nested"
  is what one NPC costs in total; "self" is the NPC alone, without its gear.
- **Per minute** figures are averages since the last `perfprobe.reset` (or plugin load).

## Use cases

### "Players report lag spikes"

1. Run `perfprobe.status` to see how often spikes happen (spikes per minute) and how bad.
2. Run `perfprobe.spikes 20` and look at the biggest frames. Each line says what took the
   time: a game method (`SpawnGroup.Spawn 280ms`), a plugin (`hook:BetterNpc`), a plugin timer
   (`timer:NukeOps`), or `GC` for garbage collection.
3. Run `perfprobe.top` for the pattern over time: which entries show up in the most spikes.
4. If the spikes come at a regular interval (every 30 minutes, at dawn and dusk...), note the
   times in `perfprobe.spikes` and look at what was spawned or killed in those frames.

### "Which plugin costs the most?"

Run `perfprobe.top 40` and look at the `hook:` and `timer:` entries under "Overall cost per
minute". Each plugin's hooks are timed as one entry (`hook:PluginName`), and each timer
separately, with its interval. Compare plugins against each other and against the game's own
entries.

### "Is something spawning and killing entities over and over?"

1. Run `perfprobe.spawns 30`. Prefabs that are spawned and killed at the same high rate are
   churning: something creates them and something else removes them straight away.
2. Run `perfprobe.spawncost <name>` to see what that churn costs in ms per minute.
3. Example from a real server: Outpost guards spawned and killed 16–17 times a minute, costing
   ~2.4ms per cycle (create 0.25ms, spawn with gear 1.7ms, kill 0.45ms).

### "Is a specific game system slow?"

1. Find the method: `perfprobe.find BaseOven` (or another type) lists its methods.
2. Time it: `perfprobe.watch BaseOven.Cook`.
3. Wait a few minutes, then check `perfprobe.top`. Remove it with `perfprobe.unwatch` when done.

Each watched call costs about 0.3 microseconds, so avoid watching methods that run hundreds of
thousands of times a second (per-entity update methods, for example). To watch something
permanently, add it to `WatchOnStartup` in the config.

### "The spike is not in any hook, timer or watched method"

Half of a server frame can sit in Unity engine systems that no plugin profiler sees.

1. Run `perfprobe.loop 30`. It prints every player-loop system with ms per second and the worst single
   call. The phases (`Update`, `PreLateUpdate`, `FixedUpdate`...) contain their children, so read the
   "systems by total" list. A hint line (`=>`) appears under anything known to be worth chasing.
2. If an engine system is busy, run the `perfprobe.components` it suggests. Example from a 4500 map with
   135,000 entities: `AIUpdatePostScript` at 207 ms/s was 10,500 `NavMeshObstacle` carvers on loot barrels
   and crates, carving a Unity navmesh that a `-useNewNavmesh` server never builds; switching them off
   took the server from 113 to 161 fps. `ConstraintManagerUpdate` at 115 ms/s was 180 `RotationConstraint`s
   on the hoof pads of 45 horses, a client rig the server evaluated every frame; off, 162 to 206 fps.
3. If a script phase is busy (`ScriptRunBehaviourUpdate`, `ScriptRunBehaviourLateUpdate`) or shows calls
   over 20 ms, run `perfprobe.behaviours 30`. It names the script. The once-a-second 55 ms frame on the same
   server was a plugin's `Update` walking every entity; the behaviours list showed it in one line.
4. `perfprobe.loop` with no arguments shows the last automatic snapshot, and `loop-YYYY-MM-DD.csv` holds
   one every few minutes, so a cost that only appears at certain times (a herd spawning, an event) shows up
   without anyone watching.

### "Garbage collection pauses"

`perfprobe.spikes` marks frames with a collection (`GC g0+1...`) and shows the heap size. The
timeline records the heap and an estimated pause length every second. If pauses grow as the heap
grows over the server's uptime, the only real fix is a scheduled restart.

### "A plugin's timer stopped working"

If a plugin throws an error at the wrong moment, Carbon can drop its repeating timers from the
schedule without destroying them. They then silently never fire again. `perfprobe.timers` lists
any live timer that is no longer scheduled; `perfprobe.timers fix` puts the repeating ones back.

### "Did my change help?"

1. Before the change, run `perfprobe.reset`, wait a fixed time (e.g. 30 minutes) and save the
   output of `perfprobe.status`, `perfprobe.top` and `perfprobe.spawns`.
2. Make the change, `perfprobe.reset` again, wait the same time, and compare.
3. For a rate, you can also take two `perfprobe.spawns` snapshots a few minutes apart without
   resetting: the difference between them is the rate in that window.

### "Is it lag from players or from the server?"

The timeline records, every second, the frame count, worst frame, Unity's own timings, heap,
entity and player counts, spikes and entity churn, plus how much time went to each category
(spawning, AI, navigation, players, game loop, physics, plugin timers, plugin hooks). Player
samples every 10 seconds record each player's position, speed and what they were doing (building,
attacking, looting, crafting, gathering). Graph the two together to see whether spikes follow
player activity.

## Files it writes

In the server's data folder, under `PerfProbe/`:

- `timeline-YYYY-MM-DD.csv`: one row per second, with the columns described above.
- `players-YYYY-MM-DD.csv`: player samples.
- `loop-YYYY-MM-DD.csv`: the top player-loop systems from each automatic snapshot (time, seconds, system,
  ms_per_s, max_ms, over20ms).

Previous days' files are gzipped, and files older than `CsvRetentionDays` are deleted.
Every spike is also written to the plugin's log file (`spikes`) in the server's logs folder.

## Config

`PerfProbe.json` in the config folder:

| Setting | Default | What it does |
|---|---|---|
| `SpikeThresholdMs` | 50 | Frames at or over this are spikes. |
| `PrintSpikesToConsole` | true | Print spikes to the console (at most one every 2 seconds). |
| `MaxStoredSpikes` | 200 | How many spike lines are kept in memory for `perfprobe.spikes`. |
| `TimePluginTimers` | true | Time each plugin timer. |
| `TimePluginHooks` | true | Time each plugin's hooks. |
| `TimeEntityCosts` | true | Time entity create/spawn/kill per prefab (`perfprobe.spawncost`). |
| `WatchOnStartup` | spawning, game loop, player, network and AI methods | Game methods timed from startup. |
| `TimelineEnabled` | true | Keep the per-second timeline. |
| `TimelineRetentionMinutes` | 180 | How much timeline is kept in memory. |
| `PlayerSampleSeconds` | 10 | How often players are sampled. |
| `MaxPlayersPerSample` | 150 | Cap on players per sample. |
| `WriteCsv` | true | Write the timeline and player samples to CSV files. |
| `CsvRetentionDays` | 7 | How long CSV files are kept. |
| `LoopSnapshotMinutes` | 5 | How often the player loop is timed for one second and the top systems kept (status, `perfprobe.loop`, loop CSV). 0 turns it off. |
| `LoopSnapshotTop` | 8 | How many systems each snapshot keeps. |

## Overhead and safety

- `perfprobe.status` shows its own overhead. On a 30-plugin server it is around 0.6ms per second
  (well under 0.1% of one core).
- All timing code is written never to throw inside game code: if a measurement fails, it is
  dropped and the game carries on.
- Unloading the plugin removes every patch it made.
- `perfprobe.entities` and `perfprobe.watch` on very busy methods are the only things that cost
  noticeably; both are opt-in. The loop snapshot adds ~170 stopwatch calls per frame for one second every
  `LoopSnapshotMinutes`. `perfprobe.behaviours` patches a few hundred methods for its window and unpatches
  them after; `perfprobe.loop` swaps the player loop in for its window and restores it after (and on
  unload).
