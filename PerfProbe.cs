using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
using UnityEngine.LowLevel;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Oxide.Plugins
{
    [Info("PerfProbe", "LucienAI", "0.5.3")]
    [Description("Server performance probe: records frame spikes and what ran during them, a per-second timeline of where main-thread time went and what players were doing, and engine probes that time Unity's own player-loop systems, every script Update, and count components by prefab; a windowed probe for every entity invoke, every server RPC timed; network traffic by message type and player connection quality.")]
    public class PerfProbe : RustPlugin
    {
        private const string HarmonyId = "com.lucienmp.perfprobe";

        private static PerfProbe _instance;

        // 0.5.3: set by perfprobe.detach; nothing re-patches until the plugin is reloaded
        private static bool _detached;
        private PluginConfig _config;
        private Harmony _harmony;
        private GameObject _host;

        #region Config

        private class PluginConfig
        {
            // Bumped when defaults change in a way existing config files should pick up
            public int ConfigVersion;

            public float SpikeThresholdMs = 50f;
            public bool PrintSpikesToConsole = true;
            public int MaxStoredSpikes = 200;
            public bool TimePluginTimers = true;
            public bool TimePluginHooks = true;

            // Per-prefab time spent creating, spawning and killing entities (perfprobe.spawncost)
            public bool TimeEntityCosts = true;

            // Type.Method or Type.* (all declared methods, plus iterator/coroutine bodies).
            // Kept to low-frequency main-loop methods: each watched call costs ~0.3us.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> WatchOnStartup = DefaultWatchList();

            // Per-second timeline of where main-thread time went, plus player activity samples
            public bool TimelineEnabled = true;
            public int TimelineRetentionMinutes = 180;
            public int PlayerSampleSeconds = 10;
            public int MaxPlayersPerSample = 150;
            public bool WriteCsv = true;
            public int CsvRetentionDays = 7;

            // 0.4.0: every N minutes the Unity player loop is timed for one second and the top systems are kept
            // (perfprobe.status, perfprobe.loop with no arguments, loop-YYYY-MM-DD.csv). 0 = off.
            public int LoopSnapshotMinutes = 5;
            public int LoopSnapshotTop = 8;

            // 0.5.0: packets and bytes sent per network message type (perfprobe.net) and engine network totals in the timeline
            public bool TrackNetwork = true;

            // 0.5.0: time every ObjectWorkQueue<T>.RunJob in the game (queue:...) from startup
            public bool WatchWorkQueues = true;

            // 0.5.0: time every console command, including RCON and client-issued ones (cmd:...)
            public bool TimeConsoleCommands = true;

            // 0.5.1: time every server RPC through BaseEntity.SV_RPCMessage (one patch; perfprobe.rpcs)
            public bool TimeRpcs = true;

            // 0.5.0: ask each client for its fps and memory every PlayerSampleSeconds (perfprobe.clients, player samples).
            // Off by default: it sends every client an RPC and hooks BasePlayer.PerformanceReport by IL, which a Rust update can break.
            public bool ClientPerfReports = false;
        }

        private static List<string> DefaultWatchList() => new List<string>
        {
            // Spawning
            "SpawnHandler.SpawnTick", "SpawnHandler.SpawnGroupTick", "SpawnHandler.SpawnIndividualTick",
            "SpawnHandler.SpawnRepeating", "SpawnPopulationBase.Fill",
            "SpawnGroup.Spawn", "SpawnGroup.SpawnInitial", "SpawnGroup.Clear", "LootContainer.SpawnLoot",
            // Game loop
            "ServerMgr.Update", "ServerMgr.FixedUpdate", "ServerMgr.LateUpdate", "ServerMgr.DoTick",
            "ServerBuildingManager.Cycle", "ServerBuildingManager.Merge", "ServerBuildingManager.Split",
            "IOEntity.ProcessQueue", "TriggerParent.RunOnTick", "TreeManager.SendPendingTrees",
            "Buoyancy.Cycle", "BaseMountable.FixedUpdateCycle",
            // Players / network
            "ServerMgr.OnNetworkMessage", "ServerMgr.OnPlayerTick", "BasePlayer.ServerCycle", "BaseMountable.PlayerSyncCycle",
            "EACServer.DoUpdate", "AntiHack.Cycle", "ConnectionQueue.Cycle", "Network.BaseNetwork.Cycle",
            // NPC AI and navigation
            "AIThinkManager.ProcessQueue", "BasePet.ProcessMovementQueue",
            "Rust.Ai.Gen2.Nav.RustNavigation.Tick", "Rust.Ai.Gen2.RustNavMeshAgent.TickEnabledComponents"
        };

        // Added to the defaults in config version 3; appended to existing lists so custom entries are kept
        private static readonly string[] WatchAddedInV3 =
        {
            "ServerBuildingManager.Merge", "ServerBuildingManager.Split", "BaseMountable.FixedUpdateCycle",
            "Network.BaseNetwork.Cycle", "BasePet.ProcessMovementQueue"
        };

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig { ConfigVersion = 4 };
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null)
                    throw new Exception("Config file is null");
            }
            catch
            {
                PrintWarning("Config invalid, generating new one.");
                LoadDefaultConfig();
            }

            // v0.1 watched SpawnHandler.* / SpawnGroup.* (71 methods, some called thousands of times a
            // minute); replace that list with the lean default once
            if (_config.ConfigVersion < 2)
            {
                _config.WatchOnStartup = DefaultWatchList();
                _config.ConfigVersion = 2;
            }

            if (_config.ConfigVersion < 3)
            {
                foreach (var spec in WatchAddedInV3)
                    if (!_config.WatchOnStartup.Contains(spec))
                        _config.WatchOnStartup.Add(spec);
                _config.ConfigVersion = 3;
            }

            // 0.5.2: the network cycle is Network.BaseNetwork.Cycle; 0.5.0/0.5.1 named a Raknet method that does not exist
            if (_config.ConfigVersion < 4)
            {
                int old = _config.WatchOnStartup.IndexOf("Facepunch.Network.Raknet.Server.Cycle");
                if (old >= 0) _config.WatchOnStartup[old] = "Network.BaseNetwork.Cycle";
                else if (!_config.WatchOnStartup.Contains("Network.BaseNetwork.Cycle")) _config.WatchOnStartup.Add("Network.BaseNetwork.Cycle");
                _config.ConfigVersion = 4;
            }

            _config.TimelineRetentionMinutes = Mathf.Clamp(_config.TimelineRetentionMinutes, 5, 24 * 60);
            _config.PlayerSampleSeconds = Mathf.Clamp(_config.PlayerSampleSeconds, 1, 300);
            _config.MaxPlayersPerSample = Mathf.Clamp(_config.MaxPlayersPerSample, 1, 500);
            _config.CsvRetentionDays = Mathf.Max(1, _config.CsvRetentionDays);
            _config.LoopSnapshotMinutes = Mathf.Clamp(_config.LoopSnapshotMinutes, 0, 24 * 60);
            _config.LoopSnapshotTop = Mathf.Clamp(_config.LoopSnapshotTop, 3, 40);
            SaveConfig();
        }

        protected override void SaveConfig() =>
            Config.WriteObject(_config, true);

        #endregion

        #region Lifecycle

        private void OnServerInitialized()
        {
            _instance = this;
            Probe.Init(_config.SpikeThresholdMs, _config.PrintSpikesToConsole, _config.MaxStoredSpikes);

            // Patched here rather than in Init(), matching the other Harmony plugins on this server
            _harmony = new Harmony(HarmonyId);
            Probe.Harmony = _harmony;

            if (_config.TimePluginTimers)
                Puts(Probe.PatchTimers());

            int watched = 0;
            var missing = new List<string>();
            foreach (var spec in _config.WatchOnStartup)
            {
                int count = Probe.WatchCount(spec, out string message);
                watched += count;
                if (count == 0) missing.Add(message);
            }
            Puts($"Watching {watched} method(s) from {_config.WatchOnStartup.Count} entries" +
                 (missing.Count > 0 ? $"; skipped: {string.Join("; ", missing)}" : string.Empty));

            if (_config.TimePluginHooks)
            {
                foreach (var plugin in plugins.GetAll())
                    Probe.PatchPluginHooks(plugin);
                Puts($"Timing plugin hooks for {Probe.HookPatchedCount} plugin(s)" +
                     (Probe.HookPatchFailures > 0 ? $", {Probe.HookPatchFailures} could not be patched" : string.Empty));
            }

            if (_config.TimeEntityCosts)
                Puts(Probe.PatchEntityCosts());

            if (_config.WatchWorkQueues)
                Puts(Probe.WatchWorkQueues());

            if (_config.TimeConsoleCommands)
                Puts(Probe.PatchConsoleCommands());

            if (_config.TrackNetwork)
                Puts(NetProbe.Patch(_harmony));

            if (_config.TimeRpcs)
                Puts(Calls.PatchRpcs(_harmony));

            if (_config.ClientPerfReports)
                Puts(Clients.Patch(this, _harmony, _config.PlayerSampleSeconds));

            if (_config.TimelineEnabled)
                Timeline.Init(_config);

            _host = new GameObject("PerfProbe");
            var watcher = _host.AddComponent<FrameWatcher>();
            Probe.Active = true;
            Engine.Init(this, _config);
            Calls.Init(this, watcher);
            Puts($"Spikes >= {Probe.ThresholdMs:0}ms are recorded; timeline {(_config.TimelineEnabled ? $"keeps {_config.TimelineRetentionMinutes} min" : "off")}. " +
                 "Use perfprobe.status / perfprobe.top / perfprobe.spikes / perfprobe.timeline.");
        }

        private void Unload()
        {
            Calls.Shutdown();
            Clients.Shutdown();
            Engine.Shutdown();
            Probe.Active = false;
            if (_host != null)
                UnityEngine.Object.Destroy(_host);
            _harmony?.UnpatchAll(HarmonyId);
            Timeline.Shutdown();
            Probe.Shutdown();
            _instance = null;
        }

        // Plugins loaded after us get their hook dispatcher timed too
        private void OnPluginLoaded(Plugin plugin)
        {
            if (Probe.Active && _config.TimePluginHooks && plugin != null)
                Probe.PatchPluginHooks(plugin);
        }

        private void OnEntitySpawned(BaseNetworkable entity) => Probe.CountSpawn(entity);

        private void OnEntityKill(BaseNetworkable entity) => Probe.CountKill(entity);

        #endregion

        #region Player activity hooks

        private void OnEntityBuilt(Planner planner, GameObject go) =>
            Timeline.CountActivity(planner?.GetOwnerPlayer(), Timeline.ActBuild);

        private void OnPlayerAttack(BasePlayer attacker, HitInfo info) =>
            Timeline.CountActivity(attacker, Timeline.ActAttack);

        private void OnLootEntity(BasePlayer player, BaseEntity entity) =>
            Timeline.CountActivity(player, Timeline.ActLoot);

        private void OnItemCraft(ItemCraftTask task, BasePlayer player, Item item) =>
            Timeline.CountActivity(player, Timeline.ActCraft);

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item) =>
            Timeline.CountActivity(entity as BasePlayer, Timeline.ActGather);

        #endregion

        #region Commands

        private bool Allowed(ConsoleSystem.Arg arg) => arg.Connection == null || arg.IsAdmin;

        private bool Attached(ConsoleSystem.Arg arg)
        {
            if (!_detached) return true;
            arg.ReplyWith("PerfProbe is detached; reload it to attach again");
            return false;
        }

        // perfprobe.detach: stop recording and remove every patch a few per frame, so the plugin can then be unloaded or
        // reloaded without the multi-second freeze of removing ~100 patches in one frame
        [ConsoleCommand("perfprobe.detach")]
        private void CmdDetach(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_detached)
            {
                arg.ReplyWith(Detacher.Running ? "PerfProbe is detaching (still removing patches)" : "PerfProbe detached, fast unload can now be performed");
                return;
            }
            _detached = true;
            arg.ReplyWith(Detacher.Start(this, _harmony));
        }

        [ConsoleCommand("perfprobe.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_detached) { arg.ReplyWith(Detacher.Running ? "PerfProbe is detaching (still removing patches)" : "PerfProbe detached, fast unload can now be performed"); return; }
            arg.ReplyWith(Probe.Status() + "\n" + Live.Status() + Calls.StatusLine() + "\n" + Timeline.Status() + Engine.StatusSuffix());
        }

        [ConsoleCommand("perfprobe.spikes")]
        private void CmdSpikes(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(Probe.RecentSpikes(arg.GetInt(0, 10)));
        }

        [ConsoleCommand("perfprobe.top")]
        private void CmdTop(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(Probe.Top(arg.GetInt(0, 15)));
        }

        [ConsoleCommand("perfprobe.spawns")]
        private void CmdSpawns(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(Probe.SpawnChurn(arg.GetInt(0, 20)));
        }

        // perfprobe.spawncost [count] [name filter]: per-prefab create/spawn/kill time, e.g.
        // "perfprobe.spawncost peacekeeper" or "perfprobe.spawncost 40"
        [ConsoleCommand("perfprobe.spawncost")]
        private void CmdSpawnCost(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            int count = 20;
            string filter = string.Empty;
            for (int i = 0; arg.HasArgs(i + 1); i++)
            {
                string value = arg.GetString(i, string.Empty);
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) count = n;
                else filter = value;
            }
            arg.ReplyWith(Probe.EntityCostReport(count, filter));
        }

        // perfprobe.timeline <from unix seconds> [max rows]: JSON rows for graphing
        [ConsoleCommand("perfprobe.timeline")]
        private void CmdTimeline(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(Timeline.RowsJson(ArgUnixTime(arg), Mathf.Clamp(arg.GetInt(1, 600), 1, 3600)));
        }

        // perfprobe.players <from unix seconds> [max rows]: JSON player activity samples
        [ConsoleCommand("perfprobe.players")]
        private void CmdPlayers(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(Timeline.PlayersJson(ArgUnixTime(arg), Mathf.Clamp(arg.GetInt(1, 2000), 1, 20000)));
        }

        private long ArgUnixTime(ConsoleSystem.Arg arg) =>
            long.TryParse(arg.GetString(0, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

        [ConsoleCommand("perfprobe.threshold")]
        private void CmdThreshold(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            float ms = arg.GetFloat(0, Probe.ThresholdMs);
            if (ms < 5f) ms = 5f;
            Probe.ThresholdMs = ms;
            _config.SpikeThresholdMs = ms;
            SaveConfig();
            arg.ReplyWith($"Spike threshold set to {ms:0}ms");
        }

        [ConsoleCommand("perfprobe.watch")]
        private void CmdWatch(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg) || !Attached(arg)) return;
            string spec = arg.GetString(0, string.Empty);
            arg.ReplyWith(string.IsNullOrEmpty(spec) ? "Usage: perfprobe.watch Type.Method | Type.* | callees:Type.Method" :
                spec.StartsWith("callees:", StringComparison.OrdinalIgnoreCase) ? Probe.Callees(spec.Substring(8), true) : Probe.Watch(spec));
        }

        [ConsoleCommand("perfprobe.unwatch")]
        private void CmdUnwatch(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            string spec = arg.GetString(0, string.Empty);
            arg.ReplyWith(string.IsNullOrEmpty(spec) ? "Usage: perfprobe.unwatch Type.Method | Type.* | prefix* (e.g. queue:*) | callees | all" : Probe.Unwatch(spec));
        }

        [ConsoleCommand("perfprobe.find")]
        private void CmdFind(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            string typeName = arg.GetString(0, string.Empty);
            arg.ReplyWith(string.IsNullOrEmpty(typeName) ? "Usage: perfprobe.find TypeName [nameFilter]" : Probe.FindMethods(typeName, arg.GetString(1, string.Empty)));
        }

        [ConsoleCommand("perfprobe.patches")]
        private void CmdPatches(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(Probe.ListForeignPatches());
        }

        // Server-console teleport for lag testing: vanilla teleportpos needs a calling player
        [ConsoleCommand("perfprobe.tp")]
        private void CmdTeleport(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs(4))
            {
                arg.ReplyWith("Usage: perfprobe.tp <player name or id> <x> <y> <z>");
                return;
            }

            var player = BasePlayer.Find(arg.GetString(0, string.Empty));
            if (player == null)
            {
                arg.ReplyWith($"Player '{arg.GetString(0, string.Empty)}' not found");
                return;
            }

            var from = player.transform.position;
            var to = new Vector3(arg.GetFloat(1), arg.GetFloat(2), arg.GetFloat(3));
            player.Teleport(to);
            arg.ReplyWith($"Teleported {player.displayName} from {from} to {player.transform.position}");
        }

        // Loops every entity in one frame (~100-150ms with ~140k entities): avoid while players are on
        [ConsoleCommand("perfprobe.entities")]
        private void CmdEntities(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(Probe.EntityCensus(arg.GetInt(0, 25)));
        }

        // perfprobe.timers [fix|fixall]: finds plugin timers that are live but no longer scheduled (e.g. a
        // batch aborted by an exception in Carbon's timer loop) and optionally puts them back
        [ConsoleCommand("perfprobe.timers")]
        private void CmdTimers(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            string mode = arg.GetString(0, string.Empty).ToLowerInvariant();
            arg.ReplyWith(TimerDoctor.Run(plugins.GetAll(), mode == "fix" || mode == "fixall", mode == "fixall"));
        }

        // perfprobe.loop [seconds] [top]: time every Unity player-loop system (physics, navmesh, constraints, script
        // phases...) for a while and print ms per second and the worst single call. No arguments: the last snapshot.
        [ConsoleCommand("perfprobe.loop")]
        private void CmdLoop(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg) || !Attached(arg)) return;
            if (!arg.HasArgs(1)) { arg.ReplyWith(Engine.LastSnapshotText()); return; }
            arg.ReplyWith(Engine.LoopStart(Mathf.Clamp(arg.GetInt(0, 30), 2, 600), arg.GetInt(1, 30), manual: true));
        }

        // perfprobe.behaviours [seconds] [top] [filter]: stopwatch every Update / LateUpdate / FixedUpdate declared by a
        // MonoBehaviour in any loaded assembly, to name the script behind a busy script phase
        [ConsoleCommand("perfprobe.behaviours")]
        private void CmdBehaviours(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg) || !Attached(arg)) return;
            arg.ReplyWith(Engine.BehavioursStart(Mathf.Clamp(arg.GetInt(0, 30), 5, 600), arg.GetInt(1, 30), arg.GetString(2, string.Empty)));
        }

        // perfprobe.components <Type> [Type...]: live Unity components of a type, by owning prefab and object
        [ConsoleCommand("perfprobe.components")]
        private void CmdComponents(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs(1)) { arg.ReplyWith("Usage: perfprobe.components <TypeName> [TypeName...]  e.g. perfprobe.components NavMeshObstacle RotationConstraint"); return; }
            var names = new List<string>();
            for (int i = 0; arg.HasArgs(i + 1); i++) names.Add(arg.GetString(i, string.Empty));
            arg.ReplyWith(Engine.Components(names));
        }

        // perfprobe.invokes [seconds] [top] [filter]: time every Invoke/InvokeRepeating callback for a while, by method and the
        // prefab it runs on. No arguments: the last result.
        [ConsoleCommand("perfprobe.invokes")]
        private void CmdInvokes(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg) || !Attached(arg)) return;
            if (!arg.HasArgs(1)) { arg.ReplyWith(Calls.LastReport(true)); return; }
            arg.ReplyWith(Calls.InvokesStart(Mathf.Clamp(arg.GetInt(0, 30), 5, 600), arg.GetInt(1, 30), arg.GetString(2, string.Empty)));
        }

        // perfprobe.rpcs [count] [filter]: every server RPC since the last reset, by RPC name and the prefab it was sent to
        [ConsoleCommand("perfprobe.rpcs")]
        private void CmdRpcs(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            int count = 30;
            string filter = string.Empty;
            for (int i = 0; arg.HasArgs(i + 1); i++)
            {
                string value = arg.GetString(i, string.Empty);
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) count = n;
                else filter = value;
            }
            arg.ReplyWith(Calls.RpcReport(count, filter));
        }

        // perfprobe.callees Type.Method: the game methods a method calls, to drill into its self time with
        // perfprobe.watch callees:Type.Method
        [ConsoleCommand("perfprobe.callees")]
        private void CmdCallees(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg) || !Attached(arg)) return;
            string spec = arg.GetString(0, string.Empty);
            arg.ReplyWith(string.IsNullOrEmpty(spec) ? "Usage: perfprobe.callees Type.Method  e.g. perfprobe.callees ServerMgr.Update" : Probe.Callees(spec, false));
        }

        // perfprobe.net [count]: packets and bytes sent per network message type, plus the engine's own totals
        [ConsoleCommand("perfprobe.net")]
        private void CmdNet(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            arg.ReplyWith(NetProbe.Report(arg.GetInt(0, 20)));
        }

        // perfprobe.clients [count] [ping|loss|fps]: connection quality per player, and client fps/memory if ClientPerfReports is on
        [ConsoleCommand("perfprobe.clients")]
        private void CmdClients(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            int count = 30;
            string sort = "ping";
            for (int i = 0; arg.HasArgs(i + 1); i++)
            {
                string value = arg.GetString(i, string.Empty);
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) count = n;
                else sort = value.ToLowerInvariant();
            }
            arg.ReplyWith(Clients.Report(count, sort));
        }

        [ConsoleCommand("perfprobe.reset")]
        private void CmdReset(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            Probe.Reset();
            NetProbe.Reset();
            Calls.ResetRpcs();
            arg.ReplyWith("PerfProbe spike, entity and network stats reset (the timeline is kept)");
        }

        #endregion

        #region Frame watcher

        private class FrameWatcher : MonoBehaviour
        {
            private long _last;

            private void Update()
            {
                if (!Probe.Active) return;
                long now = Stopwatch.GetTimestamp();
                if (_last != 0)
                {
                    double frameMs = (now - _last) * Probe.TickToMs;
                    bool gc = Probe.EndFrame(frameMs);
                    Timeline.EndFrame(frameMs, gc);
                }
                _last = now;
            }
        }

        #endregion

        #region Timer doctor

        // Carbon takes a due timer off its schedule before firing it and puts repeating ones back after.
        // If something throws in between, the timer stays live (not destroyed, callback set) but is never
        // scheduled again, so it silently stops. Members are read by reflection because some are internal.
        private static class TimerDoctor
        {
            private class Lost
            {
                public string Plugin;
                public object Instance;
                public float Delay;
                public int Repetitions;
                public int TimesTriggered;
                public bool Repeating;
            }

            public static string Run(Plugin[] plugins, bool fixRepeating, bool fixOneShots)
            {
                int total = 0, scheduled = 0, unknown = 0, pluginsChecked = 0;
                var lost = new List<Lost>();

                foreach (var plugin in plugins)
                {
                    var library = Member(Member(plugin, "timer"), "Library") ?? Member(Member(plugin, "timer"), "timer");
                    if (!(Member(library, "_timers") is System.Collections.IEnumerable set)) continue;
                    pluginsChecked++;

                    foreach (var timer in set.Cast<object>().ToList())
                    {
                        total++;
                        if (Member(timer, "Destroyed") is bool destroyed && destroyed) continue;
                        if (Member(timer, "Callback") == null || Member(timer, "Persistence") == null) continue;

                        bool? isScheduled = Member(timer, "Scheduled") as bool?;
                        if (isScheduled == null && Member(timer, "HeapIndex") is int heapIndex)
                            isScheduled = heapIndex >= 0;
                        if (isScheduled == null) { unknown++; continue; }
                        if (isScheduled.Value) { scheduled++; continue; }

                        lost.Add(new Lost
                        {
                            Plugin = plugin.Name,
                            Instance = timer,
                            Delay = Member(timer, "Delay") is float d ? d : 0f,
                            Repetitions = Member(timer, "Repetitions") is int r ? r : 0,
                            TimesTriggered = Member(timer, "TimesTriggered") is int n ? n : 0,
                            Repeating = Member(timer, "Repeating") is bool rep && rep
                        });
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Checked {pluginsChecked} plugin(s): {total} timer(s), {scheduled} live and scheduled, " +
                              $"{lost.Count} live but NOT scheduled (lost){(unknown > 0 ? $", {unknown} could not be checked" : string.Empty)}");

                int fixedCount = 0;
                foreach (var t in lost.OrderBy(t => t.Plugin))
                {
                    bool shouldFix = t.Repeating ? fixRepeating : fixOneShots;
                    string result = string.Empty;
                    if (shouldFix)
                    {
                        // Every() timers repeat forever (repetitions <= 0); Repeat(n) gets its remaining runs
                        int repetitions = t.Repetitions <= 0 ? t.Repetitions : Math.Max(1, t.Repetitions - t.TimesTriggered);
                        result = Reschedule(t.Instance, repetitions) ? " -> rescheduled" : " -> could not reschedule";
                        if (result == " -> rescheduled") fixedCount++;
                    }
                    sb.AppendLine($"  {t.Plugin}: {(t.Repeating ? "repeating" : "one-shot")} {t.Delay}s timer, " +
                                  $"repetitions {t.Repetitions}, fired {t.TimesTriggered}x{result}");
                }

                if (lost.Count > 0 && !fixRepeating)
                    sb.AppendLine("Run 'perfprobe.timers fix' to reschedule lost repeating timers ('fixall' also one-shots, which would fire late).");
                else if (fixedCount > 0)
                    sb.AppendLine($"Rescheduled {fixedCount} timer(s).");
                return sb.ToString();
            }

            private static bool Reschedule(object timer, int repetitions)
            {
                try
                {
                    var reset = AccessTools.Method(timer.GetType(), "Reset", new[] { typeof(float), typeof(int) });
                    if (reset == null) return false;
                    reset.Invoke(timer, new object[] { -1f, repetitions });
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static object Member(object obj, string name)
            {
                if (obj == null) return null;
                try
                {
                    var type = obj.GetType();
                    var prop = AccessTools.Property(type, name);
                    if (prop != null && prop.GetIndexParameters().Length == 0) return prop.GetValue(obj);
                    var field = AccessTools.Field(type, name);
                    return field?.GetValue(obj);
                }
                catch
                {
                    return null;
                }
            }
        }

        #endregion

        #region Categories

        // Where self-time is filed in the timeline. Self-time = time in a watched call minus time in
        // watched calls nested inside it, so categories add up without double counting.
        private static class Cat
        {
            // 0.5.0 appended invokes, rpcs, work_queues and console_commands (a CSV opened with the old header is set aside)
            public const int Spawning = 0, Ai = 1, Navigation = 2, Players = 3, GameLoop = 4,
                             FixedScripts = 5, Timers = 6, Hooks = 7, OtherWatched = 8,
                             Invokes = 9, Rpcs = 10, WorkQueues = 11, Console = 12, Count = 13;

            public static readonly string[] Names =
            {
                "spawning", "ai", "navigation", "players", "game_loop", "fixed_scripts", "plugin_timers", "plugin_hooks", "other_watched",
                "invokes", "rpcs", "work_queues", "console_commands"
            };

            public static int For(string key)
            {
                if (key.StartsWith("hook:", StringComparison.Ordinal)) return Hooks;
                if (key.StartsWith("timer:", StringComparison.Ordinal)) return Timers;
                if (key.StartsWith("invoke:", StringComparison.Ordinal)) return Invokes;
                if (key.StartsWith("rpc:", StringComparison.Ordinal)) return Rpcs;
                if (key.StartsWith("queue:", StringComparison.Ordinal)) return WorkQueues;
                if (key.StartsWith("cmd:", StringComparison.Ordinal) || key.StartsWith("rcon:", StringComparison.Ordinal)) return Console;
                if (key.StartsWith("SpawnHandler.", StringComparison.Ordinal) || key.StartsWith("SpawnGroup.", StringComparison.Ordinal) ||
                    key.StartsWith("SpawnPopulationBase.", StringComparison.Ordinal) || key.StartsWith("LootContainer.", StringComparison.Ordinal))
                    return Spawning;
                if (key.StartsWith("AIThinkManager.", StringComparison.Ordinal) || key.StartsWith("BasePet.", StringComparison.Ordinal)) return Ai;
                if (key.StartsWith("RustNavigation.", StringComparison.Ordinal) || key.StartsWith("RustNavMeshAgent.", StringComparison.Ordinal))
                    return Navigation;
                if (key == "BasePlayer.ServerCycle" || key == "ServerMgr.OnPlayerTick" || key == "ServerMgr.OnNetworkMessage" ||
                    key.StartsWith("ConnectionQueue.", StringComparison.Ordinal) || key.StartsWith("EACServer.", StringComparison.Ordinal) ||
                    key.StartsWith("AntiHack.", StringComparison.Ordinal) || key == "BaseMountable.PlayerSyncCycle" || key == "BaseNetwork.Cycle")
                    return Players;
                if (key == "ServerMgr.FixedUpdate" || key.StartsWith("Buoyancy.", StringComparison.Ordinal) ||
                    key == "BaseMountable.FixedUpdateCycle") return FixedScripts;
                if (key.StartsWith("ServerMgr.", StringComparison.Ordinal) || key.StartsWith("ServerBuildingManager.", StringComparison.Ordinal) ||
                    key.StartsWith("IOEntity.", StringComparison.Ordinal) || key.StartsWith("TriggerParent.", StringComparison.Ordinal) ||
                    key.StartsWith("TreeManager.", StringComparison.Ordinal))
                    return GameLoop;
                return OtherWatched;
            }
        }

        #endregion

        #region Probe

        private static class Probe
        {
            public static bool Active;
            public static Harmony Harmony;
            public static float ThresholdMs = 50f;
            public static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
            public static int HookPatchedCount, HookPatchFailures;

            private const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private const int MaxDepth = 64;

            private static bool _printSpikes;
            private static int _maxSpikes;
            private static int _mainThreadId;
            private static bool _faulted;

            private class Stat { public string Key; public long Ticks; public long SelfTicks; public int Calls; }
            private class Agg { public double TotalMs; public long Calls; public double SpikeMs; public int SpikeHits; public double WorstMs; }

            // What each patched method is recorded as
            private class Target { public string Key; public int Category; public Dictionary<object, string> InstanceKeys; }

            // 0.5.2: work-queue instances by the static field that holds them ("BaseEntity.globalQueue"), so one patch on a
            // shared generic runner (PersistentObjectWorkQueue<T>.RunList) can still name the queue it is running
            private static readonly Dictionary<object, string> InstanceNames = new Dictionary<object, string>();

            // Per-frame buckets (reset every frame) and long-running aggregates (reset by perfprobe.reset)
            private static readonly Dictionary<string, Stat> FrameStats = new Dictionary<string, Stat>();
            // The stats recorded this frame: end-of-frame work walks these, not every key ever seen (the invoke and RPC
            // windows add thousands of keys)
            private static readonly List<Stat> Touched = new List<Stat>();
            private static readonly Dictionary<string, int> FrameSpawns = new Dictionary<string, int>();
            private static readonly Dictionary<string, int> FrameKills = new Dictionary<string, int>();
            private static int _frameSpawnTotal, _frameKillTotal;

            private static readonly Dictionary<string, Agg> Aggregates = new Dictionary<string, Agg>();
            private static readonly Dictionary<string, int> SpikeSpawnTotals = new Dictionary<string, int>();
            private static readonly Dictionary<string, int> SpawnTotals = new Dictionary<string, int>();
            private static readonly Dictionary<string, int> KillTotals = new Dictionary<string, int>();
            private static readonly List<string> Spikes = new List<string>();

            private static readonly Dictionary<MethodBase, Target> Targets = new Dictionary<MethodBase, Target>();
            private static readonly HashSet<MethodBase> Watched = new HashSet<MethodBase>();
            private static readonly HashSet<Type> HookPatchedTypes = new HashSet<Type>();
            private static MethodBase _timerMethod;

            // ConsoleSystem.Internal, timed per command
            private static MethodBase _consoleMethod;
            private static readonly Dictionary<object, string> CommandKeys = new Dictionary<object, string>();
            private static int _workQueues;

            // Self-time stack: child time accumulated per nesting level
            private static readonly long[] ChildTicks = new long[MaxDepth];
            private static int _depth;

            // Timer instance -> key, worked out once per timer (timers are long-lived)
            private static readonly Dictionary<object, string> TimerKeys = new Dictionary<object, string>();
            private static PropertyInfo _timerPluginProp, _timerDelayProp;

            private static long _frames, _spikeCount, _instrumentedCalls;
            private static double _spikeMsTotal, _worstFrameMs;
            private static float _statsSince;
            private static int _g0;
            private static float _lastPrint;

            // Per-second history for the last 60 seconds
            private static readonly int[] SecFrames = new int[60];
            private static readonly double[] SecWorst = new double[60];
            private static int _curSecond = -1, _secFrames;
            private static double _secWorst;

            public static void Init(float thresholdMs, bool printSpikes, int maxSpikes)
            {
                ThresholdMs = thresholdMs;
                _printSpikes = printSpikes;
                _maxSpikes = Math.Max(10, maxSpikes);
                _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                _g0 = GC.CollectionCount(0);
                _statsSince = Time.realtimeSinceStartup;
                _faulted = false;
                _depth = 0;
            }

            public static void Shutdown()
            {
                Watched.Clear();
                Targets.Clear();
                HookPatchedTypes.Clear();
                TimerKeys.Clear();
                CommandKeys.Clear();
                CalleeWatched.Clear();
                InstanceNames.Clear();
                Touched.Clear();
                _timerMethod = null;
                _consoleMethod = null;
                _workQueues = 0;
                Harmony = null;
                HookPatchedCount = HookPatchFailures = 0;
                _entityPatched = _entityDepth = 0;
                Array.Clear(EntityOwners, 0, EntityOwners.Length);
                FrameEntityMs.Clear();
            }

            public static void Reset()
            {
                Aggregates.Clear();
                SpikeSpawnTotals.Clear();
                SpawnTotals.Clear();
                KillTotals.Clear();
                foreach (var costs in EntityCosts) costs.Clear();
                Spikes.Clear();
                _frames = _spikeCount = _instrumentedCalls = 0;
                _spikeMsTotal = _worstFrameMs = 0;
                _statsSince = Time.realtimeSinceStartup;
            }

            #region Harmony timing

            // What a timed call remembers between its prefix and finalizer
            public struct CallState
            {
                public long Start;
                public int Depth;
            }

            // These run inside game methods and Carbon's timer loop, so they must never throw: an exception
            // here once aborted a batch of Carbon timers (dropping them from the schedule) and a
            // ServerMgr.LateUpdate. On any error the measurement is dropped and the stack reset.
            public static void TimingPrefix(out CallState __state)
            {
                __state = default;
                try
                {
                    if (_depth < 0 || _depth > MaxDepth) _depth = 0;
                    if (_depth >= MaxDepth || System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId) return;
                    ChildTicks[_depth] = 0;
                    __state.Depth = _depth;
                    __state.Start = Stopwatch.GetTimestamp();
                    _depth++;
                }
                catch
                {
                    _depth = 0;
                    __state = default;
                }
            }

            public static void TimingFinalizer(MethodBase __originalMethod, CallState __state)
            {
                if (__state.Start == 0) return;
                try
                {
                    long elapsed = Pop(__state, out long self);
                    if (!Active || !Targets.TryGetValue(__originalMethod, out var target)) return;
                    Record(target.Key, target.Category, elapsed, self);
                }
                catch
                {
                    _depth = 0;
                }
            }

            // For instance methods on generic types: one compiled body serves every reference-type instantiation, so the
            // key comes from the instance (its static field name, else its closed type), not from the patched method
            public static void InstanceFinalizer(object __instance, MethodBase __originalMethod, CallState __state)
            {
                if (__state.Start == 0) return;
                try
                {
                    long elapsed = Pop(__state, out long self);
                    if (!Active || !Targets.TryGetValue(__originalMethod, out var target)) return;
                    string key = target.Key;
                    int category = target.Category;
                    if (__instance != null)
                    {
                        if (target.InstanceKeys == null) target.InstanceKeys = new Dictionary<object, string>();
                        if (!target.InstanceKeys.TryGetValue(__instance, out key))
                        {
                            if (target.InstanceKeys.Count > 500) target.InstanceKeys.Clear();
                            key = InstanceKey(__instance, __originalMethod.Name);
                            target.InstanceKeys[__instance] = key;
                        }
                        if (key.StartsWith("queue:", StringComparison.Ordinal)) category = Cat.WorkQueues;
                    }
                    Record(key, category, elapsed, self);
                }
                catch
                {
                    _depth = 0;
                }
            }

            private static string InstanceKey(object instance, string method)
            {
                var type = instance.GetType();
                string name = InstanceNames.TryGetValue(instance, out var field) ? field : ClosedName(type);
                bool queue = false;
                for (var t = type; t != null && !queue; t = t.BaseType) queue = t.Name.IndexOf("WorkQueue", StringComparison.Ordinal) >= 0;
                return (queue ? "queue:" : string.Empty) + name + "." + method;
            }

            private static string ClosedName(Type type)
            {
                if (!type.IsGenericType) return type.Name;
                string name = type.Name;
                int tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                return name + "<" + string.Join(",", type.GetGenericArguments().Select(a => a.Name)) + ">";
            }

            public static void TimerFinalizer(object[] __args, CallState __state)
            {
                if (__state.Start == 0) return;
                try
                {
                    long elapsed = Pop(__state, out long self);
                    if (!Active) return;
                    object timerInstance = __args != null && __args.Length > 0 ? __args[0] : null;
                    Record(TimerKey(timerInstance), Cat.Timers, elapsed, self);
                }
                catch
                {
                    _depth = 0;
                }
            }

            // Restores the caller's nesting level from the call's own state instead of decrementing a
            // shared counter, so a reset or a missed finalizer (e.g. a plugin reload issued from inside a
            // timed ServerMgr.DoTick) can never leave the stack unbalanced
            public static long Pop(CallState state, out long self)
            {
                long elapsed = Stopwatch.GetTimestamp() - state.Start;
                _depth = state.Depth;
                self = Math.Max(0, elapsed - ChildTicks[state.Depth]);
                if (state.Depth > 0) ChildTicks[state.Depth - 1] += elapsed;
                return elapsed;
            }

            public static void ResetDepth() => _depth = 0;

            public static void Record(string key, int category, long ticks, long selfTicks)
            {
                _instrumentedCalls++;
                if (!FrameStats.TryGetValue(key, out var stat))
                    FrameStats[key] = stat = new Stat { Key = key };
                if (stat.Calls == 0) Touched.Add(stat);
                stat.Ticks += ticks;
                stat.SelfTicks += selfTicks;
                stat.Calls++;
                Timeline.AddSelfTime(category, selfTicks * TickToMs);
            }

            private static string TimerKey(object timerInstance)
            {
                if (timerInstance == null) return "timer:unknown";
                if (TimerKeys.TryGetValue(timerInstance, out var key)) return key;

                if (TimerKeys.Count > 5000) TimerKeys.Clear(); // destroyed timers are never removed otherwise
                _timerPluginProp ??= AccessTools.Property(timerInstance.GetType(), "Plugin");
                _timerDelayProp ??= AccessTools.Property(timerInstance.GetType(), "Delay");
                object plugin = _timerPluginProp?.GetValue(timerInstance);
                string name = plugin is Plugin p ? p.Name : "unknown";
                object delay = _timerDelayProp?.GetValue(timerInstance);
                key = delay == null ? $"timer:{name}" : $"timer:{name} ({delay}s timer)";
                TimerKeys[timerInstance] = key;
                return key;
            }

            private static HarmonyMethod Prefix() =>
                new HarmonyMethod(typeof(Probe), nameof(TimingPrefix)) { priority = Priority.First };

            public static string PatchTimers()
            {
                var type = AccessTools.TypeByName("Oxide.Core.Libraries.Timer");
                if (type == null)
                    return "Plugin timer timing unavailable: Oxide.Core.Libraries.Timer not found";

                var method = type.GetMethods(AllDeclared).FirstOrDefault(m => m.Name == "FireTimer")
                             ?? type.GetMethods(AllDeclared).FirstOrDefault(m => m.Name == "FireCollectedTimer");
                if (method == null)
                    return "Plugin timer timing unavailable: Timer.FireTimer not found on this Carbon build";

                try
                {
                    Harmony.Patch(method, prefix: Prefix(),
                        finalizer: new HarmonyMethod(typeof(Probe), nameof(TimerFinalizer)));
                    _timerMethod = method;
                    return $"Timing plugin timers via {type.Name}.{method.Name}";
                }
                catch (Exception e)
                {
                    return "Plugin timer timing failed: " + e.Message;
                }
            }

            // Times a plugin's generated hook dispatcher, so hook time is measured directly (no per-frame
            // reflection) and fits the self-time stack like everything else
            public static void PatchPluginHooks(Plugin plugin)
            {
                var type = plugin.GetType();
                if (HookPatchedTypes.Contains(type)) return;

                var method = type.GetMethod("InternalCallHook", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(uint), typeof(object[]) }, null);
                if (method == null || method.DeclaringType != type)
                {
                    HookPatchFailures++;
                    return;
                }

                try
                {
                    Targets[method] = new Target { Key = "hook:" + plugin.Name, Category = Cat.Hooks };
                    Harmony.Patch(method, prefix: Prefix(), finalizer: new HarmonyMethod(typeof(Probe), nameof(TimingFinalizer)));
                    HookPatchedTypes.Add(type);
                    HookPatchedCount++;
                }
                catch (Exception e)
                {
                    Targets.Remove(method);
                    HookPatchFailures++;
                    _instance?.PrintWarning($"Could not time hooks of {plugin.Name}: {e.Message}");
                }
            }

            public static string Watch(string spec)
            {
                int count = WatchCount(spec, out string message);
                return message;
            }

            public static int WatchCount(string spec, out string message)
            {
                int dot = spec.LastIndexOf('.');
                if (dot <= 0 || dot == spec.Length - 1)
                {
                    message = $"{spec}: use Type.Method or Type.*";
                    return 0;
                }

                string typeName = spec.Substring(0, dot);
                string methodName = spec.Substring(dot + 1);
                var type = FindType(typeName);
                if (type == null)
                {
                    message = $"{spec}: type '{typeName}' not found";
                    return 0;
                }

                bool wildcard = methodName == "*";
                var targets = new List<MethodBase>();
                foreach (var method in type.GetMethods(AllDeclared))
                {
                    if (!wildcard && method.Name != methodName) continue;
                    if (wildcard && method.IsSpecialName) continue;
                    if (!IsPatchable(method)) continue;
                    targets.Add(method);

                    var moveNext = StateMachineMoveNext(method);
                    if (moveNext != null)
                        targets.Add(moveNext);
                }

                if (targets.Count == 0)
                {
                    message = $"{spec}: no patchable methods found";
                    return 0;
                }

                int patched = 0, failed = 0;
                string lastError = null;
                foreach (var target in targets)
                {
                    if (Watched.Contains(target)) continue;
                    string key = Describe(target);
                    if (WatchMethod(target, key, Cat.For(key), out string error)) patched++;
                    else { failed++; lastError = error; }
                }

                message = $"{spec}: watching {patched} method(s)" + (failed > 0 ? $", {failed} failed (last: {lastError})" : string.Empty);
                return patched;
            }

            private static bool WatchMethod(MethodBase target, string key, int category, out string error)
            {
                error = null;
                try
                {
                    Targets[target] = new Target { Key = key, Category = category };
                    bool perInstance = !target.IsStatic && target.DeclaringType != null && target.DeclaringType.IsGenericType;
                    Harmony.Patch(target, prefix: Prefix(),
                        finalizer: new HarmonyMethod(typeof(Probe), perInstance ? nameof(InstanceFinalizer) : nameof(TimingFinalizer)));
                    Watched.Add(target);
                    return true;
                }
                catch (Exception e)
                {
                    Targets.Remove(target);
                    error = $"{key}: {e.Message}";
                    return false;
                }
            }

            // Every ObjectWorkQueue<T> in the game runs its jobs through RunJob. Found by base type, so queues added by a
            // Rust update are picked up without a config change.
            public static string WatchWorkQueues()
            {
                int failed = 0;
                string lastError = null;
                foreach (var type in GameTypes())
                {
                    bool isQueue = false;
                    try
                    {
                        if (type.ContainsGenericParameters) continue;
                        for (var b = type.BaseType; b != null && !isQueue; b = b.BaseType)
                            isQueue = b.IsGenericType && b.Name.StartsWith("ObjectWorkQueue", StringComparison.Ordinal);
                    }
                    catch { continue; }
                    if (!isQueue) continue;

                    foreach (var run in type.GetMethods(AllDeclared).Where(m => m.Name == "RunJob" && IsPatchable(m)))
                    {
                        if (Watched.Contains(run)) continue;
                        string name = type.IsNested && type.DeclaringType != null ? type.DeclaringType.Name + "." + type.Name : type.Name;
                        if (WatchMethod(run, "queue:" + name, Cat.WorkQueues, out string error)) _workQueues++;
                        else { failed++; lastError = error; }
                    }
                }
                // 0.5.2: the queues themselves. Every static ObjectWorkQueue / PersistentObjectWorkQueue field is named, and
                // each runner (RunQueue, RunList) is patched once per generic definition; the per-instance finalizer splits it
                // by queue. On a 138k-entity server the persistent queues were ~320 ms/s of ServerMgr.Update no watch named.
                int runners = 0, named = 0;
                var patchedRunners = new HashSet<string>();
                foreach (var type in GameTypes())
                {
                    FieldInfo[] fields;
                    try
                    {
                        if (type.ContainsGenericParameters) continue;
                        fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }
                    foreach (var field in fields)
                    {
                        if (!IsQueueType(field.FieldType)) continue;
                        object queue;
                        try { queue = field.GetValue(null); } catch { continue; }
                        if (queue == null) continue;
                        if (!InstanceNames.ContainsKey(queue)) { InstanceNames[queue] = $"{type.Name}.{field.Name}"; named++; }

                        foreach (var run in RunnerMethods(queue.GetType()))
                        {
                            var owner = run.DeclaringType;
                            string id = (owner.IsGenericType ? owner.GetGenericTypeDefinition().FullName : owner.FullName) + "." + run.Name;
                            if (!patchedRunners.Add(id) || Watched.Contains(run)) continue;
                            if (WatchMethod(run, Describe(run), Cat.WorkQueues, out string error)) runners++;
                            else { failed++; lastError = error; }
                        }
                    }
                }

                return $"Timing {_workQueues} work queue job runner(s) (RunJob) and {named} named queue(s) through {runners} queue runner(s)" +
                       (failed > 0 ? $", {failed} failed (last: {lastError})" : string.Empty);
            }

            private static bool IsQueueType(Type type)
            {
                for (var t = type; t != null; t = t.BaseType)
                    if (t.Name.StartsWith("ObjectWorkQueue", StringComparison.Ordinal) || t.Name.StartsWith("PersistentObjectWorkQueue", StringComparison.Ordinal))
                        return true;
                return false;
            }

            // The most derived RunQueue / RunList with a body
            private static IEnumerable<MethodInfo> RunnerMethods(Type type)
            {
                foreach (var name in new[] { "RunQueue", "RunList" })
                {
                    for (var t = type; t != null; t = t.BaseType)
                    {
                        var m = t.GetMethods(AllDeclared).FirstOrDefault(x => x.Name == name && !x.IsStatic && IsPatchable(x));
                        if (m == null) continue;
                        yield return m;
                        break;
                    }
                }
            }

            // AccessTools.TypeByName walks every type of every assembly for a short name (~0.9 s on a big server, in one
            // frame); the game's own assemblies answer most names at once
            public static Type FindType(string name)
            {
                foreach (var asm in new[] { typeof(BaseNetworkable).Assembly, typeof(ConsoleSystem).Assembly, typeof(global::Network.Net).Assembly })
                {
                    try
                    {
                        var type = asm.GetType(name, false);
                        if (type != null) return type;
                    }
                    catch { }
                }
                return AccessTools.TypeByName(name);
            }

            public static Type[] GameTypes()
            {
                try { return typeof(BaseNetworkable).Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
            }

            // 0.5.1: RunWithResult, the entry every command goes through, with a prefix that runs ahead of all others.
            // Carbon dispatches its own commands from a prefix on ConsoleSystem; on 0.5.0 that prefix ran before ours
            // (both at Priority.First, Carbon's patched first) so plugin commands were never timed.
            public static string PatchConsoleCommands()
            {
                var method = typeof(ConsoleSystem).GetMethods(AllDeclared).FirstOrDefault(m =>
                    m.Name == "RunWithResult" && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(string));
                if (method == null)
                    return "Console command timing unavailable: ConsoleSystem.RunWithResult(Option, string, object[]) not found";
                try
                {
                    Harmony.Patch(method, prefix: new HarmonyMethod(typeof(Probe), nameof(TimingPrefix)) { priority = AheadOfEverything },
                        finalizer: new HarmonyMethod(typeof(Probe), nameof(ConsoleFinalizer)));
                    _consoleMethod = method;
                }
                catch (Exception e)
                {
                    return "Console command timing failed: " + e.Message;
                }

                // 0.5.2: Carbon runs RCON commands from its own prefix on Facepunch.RCon.OnCommand, so they never reach
                // RunWithResult; timed there too, as rcon:<command>
                string rcon = "; RCON commands not timed (Facepunch.RCon.OnCommand not found)";
                var rconType = AccessTools.TypeByName("Facepunch.RCon");
                var onCommand = rconType?.GetMethods(AllDeclared).FirstOrDefault(m => m.Name == "OnCommand" && m.GetParameters().Length == 1);
                if (onCommand != null)
                {
                    try
                    {
                        Harmony.Patch(onCommand, prefix: new HarmonyMethod(typeof(Probe), nameof(TimingPrefix)) { priority = AheadOfEverything },
                            finalizer: new HarmonyMethod(typeof(Probe), nameof(RconFinalizer)));
                        rcon = " and Facepunch.RCon.OnCommand";
                    }
                    catch (Exception e)
                    {
                        rcon = "; RCON command timing failed: " + e.Message;
                    }
                }
                return "Timing console commands via ConsoleSystem.RunWithResult" + rcon;
            }

            public static void RconFinalizer(object __0, CallState __state)
            {
                if (__state.Start == 0) return;
                try
                {
                    long elapsed = Pop(__state, out long self);
                    if (!Active) return;
                    Record("rcon:" + CommandKey(Reflect.Get(__0, "Message") as string).Substring(4), Cat.Console, elapsed, self);
                }
                catch
                {
                    _depth = 0;
                }
            }

            // Harmony runs higher priorities first; Priority.First is 800
            private const int AheadOfEverything = 100000;

            public static void ConsoleFinalizer(string __1, CallState __state)
            {
                if (__state.Start == 0) return;
                try
                {
                    long elapsed = Pop(__state, out long self);
                    if (!Active) return;
                    Record(CommandKey(__1), Cat.Console, elapsed, self);
                }
                catch
                {
                    _depth = 0;
                }
            }

            // The command name as typed (first word), lower-cased: "perfprobe.status", "c.reload", "inventory.endloot"
            private static string CommandKey(string command)
            {
                if (string.IsNullOrEmpty(command)) return "cmd:unknown";
                command = command.TrimStart();
                int space = command.IndexOf(' ');
                string name = space < 0 ? command : command.Substring(0, space);
                if (CommandKeys.TryGetValue(name, out var key)) return key;
                if (CommandKeys.Count > 5000) CommandKeys.Clear();
                key = "cmd:" + name.Trim('"').ToLowerInvariant();
                CommandKeys[name] = key;
                return key;
            }

            // 0.5.1: the game methods a method calls, read from its IL, so a large self time can be split one level down.
            // Virtual calls are listed as the base method: watching it does not time the overrides.
            private static readonly HashSet<MethodBase> CalleeWatched = new HashSet<MethodBase>();

            public static string Callees(string spec, bool watch)
            {
                int dot = spec.LastIndexOf('.');
                if (dot <= 0 || dot == spec.Length - 1) return $"{spec}: use Type.Method";
                var type = FindType(spec.Substring(0, dot));
                if (type == null) return $"{spec}: type '{spec.Substring(0, dot)}' not found";
                string methodName = spec.Substring(dot + 1);
                var methods = type.GetMethods(AllDeclared).Where(m => m.Name == methodName).ToList();
                if (methods.Count == 0) return $"{spec}: no method named {methodName}";

                var callees = new List<MethodInfo>();
                var sources = new Dictionary<string, List<string>>();
                int skipped = 0;
                foreach (var method in methods)
                {
                    List<CodeInstruction> instructions;
                    try { instructions = PatchProcessor.GetOriginalInstructions(method); }
                    catch (Exception e) { return $"{spec}: could not read its IL ({e.Message})"; }
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        var ci = instructions[i];
                        if ((ci.opcode != OpCodes.Call && ci.opcode != OpCodes.Callvirt) || !(ci.operand is MethodInfo callee)) continue;

                        // A queue-style call usually runs on an instance loaded from a static field a few instructions back
                        if (!callee.IsStatic && callee.DeclaringType != null)
                        {
                            for (int back = i - 1; back >= Math.Max(0, i - 6); back--)
                            {
                                if (instructions[back].opcode != OpCodes.Ldsfld || !(instructions[back].operand is FieldInfo field) ||
                                    !callee.DeclaringType.IsAssignableFrom(field.FieldType)) continue;
                                string fieldName = $"{field.DeclaringType?.Name}.{field.Name}";
                                string calleeKey = Describe(callee);
                                if (!sources.TryGetValue(calleeKey, out var list)) sources[calleeKey] = list = new List<string>();
                                if (!list.Contains(fieldName)) list.Add(fieldName);
                                try
                                {
                                    var value = field.GetValue(null);
                                    if (value != null && !InstanceNames.ContainsKey(value)) InstanceNames[value] = fieldName;
                                }
                                catch { }
                                break;
                            }
                        }

                        if (callees.Contains(callee)) continue;
                        if (InterestingCallee(callee)) callees.Add(callee);
                        else skipped++;
                    }
                }
                if (callees.Count == 0) return $"{spec} calls no patchable game methods ({skipped} framework/accessor calls skipped)";

                // Closed generics (ObjectWorkQueue<A>.RunQueue, ObjectWorkQueue<B>.RunQueue...) share one compiled body for
                // reference types and one key: keep one of each, with how many it stands for
                var copies = callees.GroupBy(Describe).ToDictionary(g => g.Key, g => g.Count());
                callees = callees.GroupBy(Describe).Select(g => g.First()).ToList();

                if (!watch)
                {
                    var sb = new StringBuilder($"{spec} calls {callees.Count} patchable game method(s) ({skipped} framework/accessor calls skipped); " +
                                               $"'perfprobe.watch callees:{spec}' times them all, 'perfprobe.unwatch callees' stops:\n");
                    foreach (var callee in callees)
                        sb.Append($"  {Describe(callee)}{(copies[Describe(callee)] > 1 ? $" (x{copies[Describe(callee)]} generic instances)" : string.Empty)}" +
                                  $"{(callee.IsVirtual && !callee.IsFinal ? "  [virtual]" : string.Empty)}{(Watched.Contains(callee) ? "  [watched]" : string.Empty)}" +
                                  (sources.TryGetValue(Describe(callee), out var on) ? $"  on {string.Join(", ", on.Take(20))}{(on.Count > 20 ? $" (+{on.Count - 20})" : string.Empty)}" : string.Empty) + "\n");
                    return sb.ToString().TrimEnd();
                }

                // Patched over several frames (each Harmony patch costs a few ms)
                var todo = callees.Where(c => !Watched.Contains(c)).ToList();
                var steps = todo.Select(c => (Action)(() =>
                {
                    string key = Describe(c);
                    if (WatchMethod(c, key, Cat.For(key), out _)) CalleeWatched.Add(c);
                })).ToList();
                if (!Calls.Budgeted(steps, () => _instance?.Puts($"[PerfProbe] watching {CalleeWatched.Count} callee(s) of {spec}; see perfprobe.top, 'perfprobe.unwatch callees' to stop")))
                    return "PerfProbe not initialised";
                return $"Watching {todo.Count} callee(s) of {spec} ({callees.Count - todo.Count} already watched), patched over the next few frames; " +
                       "their time shows in perfprobe.top and perfprobe.spikes (inclusive), and comes out of the caller's self time";
            }

            private static bool InterestingCallee(MethodInfo m)
            {
                try
                {
                    if (m.IsSpecialName || m.IsGenericMethod || !IsPatchable(m)) return false;
                    var type = m.DeclaringType;
                    if (type == null || type == typeof(PerfProbe) || type.FullName.StartsWith("Oxide.Plugins.PerfProbe", StringComparison.Ordinal)) return false;
                    string assembly = type.Assembly.GetName().Name;
                    if (assembly == "mscorlib" || assembly == "netstandard" || assembly.StartsWith("System", StringComparison.Ordinal)) return false;
                    if (type.Name == "TimeWarning" || type.Namespace == "Facepunch" && type.Name == "Pool") return false;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public static string Unwatch(string spec)
            {
                bool all = spec == "all";
                if (spec == "callees")
                {
                    int removed = 0;
                    foreach (var target in CalleeWatched.ToList())
                    {
                        try { Harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId); } catch { }
                        Watched.Remove(target);
                        Targets.Remove(target);
                        removed++;
                    }
                    CalleeWatched.Clear();
                    return $"Unwatched {removed} callee(s)";
                }

                // Type.* or any key prefix ending in * (queue:*, cmd:...)
                bool wildcard = spec.EndsWith("*");
                string prefix = wildcard ? spec.Substring(0, spec.Length - 1) : null;

                var targets = Watched.Where(m =>
                {
                    if (all) return true;
                    string key = Targets.TryGetValue(m, out var t) ? t.Key : Describe(m);
                    return wildcard ? key.StartsWith(prefix, StringComparison.Ordinal) : key == spec;
                }).ToList();

                foreach (var target in targets)
                {
                    try { Harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId); } catch { }
                    Watched.Remove(target);
                    Targets.Remove(target);
                }

                return $"Unwatched {targets.Count} method(s)";
            }

            public static bool IsPatchable(MethodInfo method)
            {
                if (method.IsAbstract || method.IsGenericMethodDefinition || method.ContainsGenericParameters) return false;
                if (method.DeclaringType == null || method.DeclaringType.ContainsGenericParameters) return false;
                if (method.ReturnType.IsByRef) return false;
                try { return method.GetMethodBody() != null; }
                catch { return false; }
            }

            private static MethodInfo StateMachineMoveNext(MethodInfo method)
            {
                var attr = method.GetCustomAttribute<System.Runtime.CompilerServices.StateMachineAttribute>();
                var smType = attr?.StateMachineType;
                if (smType == null || smType.ContainsGenericParameters) return null;
                var moveNext = smType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return moveNext != null && IsPatchable(moveNext) ? moveNext : null;
            }

            private static string Describe(MethodBase method)
            {
                var type = method.DeclaringType;
                if (type == null) return method.Name;

                // Iterator/async bodies live in nested types like <SpawnTick>d__12
                if (type.IsNested && type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null)
                {
                    int end = type.Name.IndexOf('>');
                    string inner = end > 1 ? type.Name.Substring(1, end - 1) : type.Name;
                    return $"{type.DeclaringType.Name}.{inner}[body]";
                }

                return $"{type.Name}.{method.Name}";
            }

            public static string FindMethods(string typeName, string filter)
            {
                var type = FindType(typeName);
                if (type == null)
                    return $"Type '{typeName}' not found";

                var lines = type.GetMethods(AllDeclared)
                    .Where(m => !m.IsSpecialName && (filter.Length == 0 || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(m =>
                    {
                        bool iterator = m.GetCustomAttribute<System.Runtime.CompilerServices.StateMachineAttribute>() != null;
                        string ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                        return $"  {(m.IsStatic ? "static " : string.Empty)}{m.ReturnType.Name} {m.Name}({ps}){(iterator ? "  [iterator/async]" : string.Empty)}";
                    })
                    .Distinct()
                    .OrderBy(s => s)
                    .Take(120)
                    .ToList();

                return $"{type.FullName} ({lines.Count} shown, base {type.BaseType?.Name}):\n" + string.Join("\n", lines);
            }

            public static string ListForeignPatches()
            {
                var sb = new StringBuilder("Harmony patches applied by other plugins/extensions:\n");
                int count = 0;
                foreach (var method in Harmony.GetAllPatchedMethods())
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    var owners = info.Owners.Where(o => o != HarmonyId).ToList();
                    if (owners.Count == 0) continue;
                    sb.Append($"  {method.DeclaringType?.FullName}.{method.Name} <- {string.Join(", ", owners)}");
                    sb.Append($" [pre {info.Prefixes.Count}, post {info.Postfixes.Count}, trans {info.Transpilers.Count}]\n");
                    if (++count >= 150) { sb.Append("  ... truncated\n"); break; }
                }
                return count == 0 ? "No foreign Harmony patches found" : sb.ToString();
            }

            #endregion

            #region Entities

            public static void CountSpawn(BaseNetworkable entity)
            {
                if (!Active || entity == null) return;
                string name = entity.ShortPrefabName ?? "unknown";
                FrameSpawns[name] = FrameSpawns.TryGetValue(name, out var c) ? c + 1 : 1;
                SpawnTotals[name] = SpawnTotals.TryGetValue(name, out var t) ? t + 1 : 1;
                _frameSpawnTotal++;
                Timeline.CountEntity(true);
            }

            public static void CountKill(BaseNetworkable entity)
            {
                if (!Active || entity == null) return;
                string name = entity.ShortPrefabName ?? "unknown";
                FrameKills[name] = FrameKills.TryGetValue(name, out var c) ? c + 1 : 1;
                KillTotals[name] = KillTotals.TryGetValue(name, out var t) ? t + 1 : 1;
                _frameKillTotal++;
                Timeline.CountEntity(false);
            }

            #endregion

            #region Entity costs

            // Per-prefab time spent in GameManager.CreateEntity, Spawn() and Kill(). These nest (an NPC's
            // Spawn creates and spawns its gear, its Kill kills it), so each prefab keeps inclusive time and
            // self time (without the nested entities). Uses a stack of its own so the method timings above
            // are unchanged. Called ~3 times per entity, so the overhead is a few hundred calls a minute.
            public const int PhaseCreate = 0, PhaseSpawn = 1, PhaseKill = 2, PhaseCount = 3;
            private static readonly string[] PhaseNames = { "create", "spawn", "kill" };

            private class EntityCost { public long Count; public double TotalMs, SelfMs, WorstMs; }

            private static readonly Dictionary<string, EntityCost>[] EntityCosts =
            {
                new Dictionary<string, EntityCost>(), new Dictionary<string, EntityCost>(), new Dictionary<string, EntityCost>()
            };
            private static readonly Dictionary<string, double> FrameEntityMs = new Dictionary<string, double>();

            private static readonly long[] EntityChildTicks = new long[MaxDepth];
            private static readonly object[] EntityOwners = new object[MaxDepth];
            private static readonly int[] EntityPhases = new int[MaxDepth];
            private static int _entityDepth;
            private static int _entityPatched;

            public struct EntityState
            {
                public long Start;
                public int Depth;
            }

            public static string PatchEntityCosts()
            {
                var targets = new List<(MethodBase Method, string Prefix, string Finalizer)>();
                var create = typeof(GameManager).GetMethods(AllDeclared)
                    .FirstOrDefault(m => m.Name == "CreateEntity" && typeof(BaseEntity).IsAssignableFrom(m.ReturnType));
                if (create != null) targets.Add((create, nameof(CreatePrefix), nameof(CreateFinalizer)));

                // BaseEntity.Spawn calls BaseNetworkable.Spawn: both are patched (subclasses may call either)
                // and the inner call for the same entity is skipped in SpawnPrefix
                foreach (var type in new[] { typeof(BaseNetworkable), typeof(BaseEntity) })
                {
                    var spawn = type.GetMethod("Spawn", AllDeclared, null, Type.EmptyTypes, null);
                    if (spawn != null) targets.Add((spawn, nameof(SpawnPrefix), nameof(SpawnFinalizer)));
                }

                var kill = typeof(BaseNetworkable).GetMethods(AllDeclared)
                    .Where(m => m.Name == "Kill").OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
                if (kill != null) targets.Add((kill, nameof(KillPrefix), nameof(KillFinalizer)));

                var failed = new List<string>();
                foreach (var target in targets)
                {
                    try
                    {
                        Harmony.Patch(target.Method,
                            prefix: new HarmonyMethod(typeof(Probe), target.Prefix) { priority = Priority.First },
                            finalizer: new HarmonyMethod(typeof(Probe), target.Finalizer));
                        _entityPatched++;
                    }
                    catch (Exception e)
                    {
                        failed.Add($"{Describe(target.Method)}: {e.Message}");
                    }
                }

                string missing = (create == null ? " CreateEntity not found;" : string.Empty) + (kill == null ? " Kill not found;" : string.Empty);
                return $"Timing entity create/spawn/kill per prefab via {_entityPatched} method(s)" +
                       (missing.Length > 0 || failed.Count > 0 ? $" (problems:{missing} {string.Join("; ", failed)})" : string.Empty);
            }

            public static void CreatePrefix(out EntityState __state) => Push(PhaseCreate, null, out __state);

            public static void SpawnPrefix(BaseNetworkable __instance, out EntityState __state) => Push(PhaseSpawn, __instance, out __state);

            // Kill on an entity that is already destroyed returns at once: not counted
            public static void KillPrefix(BaseNetworkable __instance, out EntityState __state)
            {
                __state = default;
                try
                {
                    if ((object)__instance == null || __instance.IsDestroyed) return;
                }
                catch
                {
                    return;
                }
                Push(PhaseKill, __instance, out __state);
            }

            public static void CreateFinalizer(BaseEntity __result, EntityState __state) => PopEntity(PhaseCreate, __result, __state);

            public static void SpawnFinalizer(BaseNetworkable __instance, EntityState __state) => PopEntity(PhaseSpawn, __instance, __state);

            public static void KillFinalizer(BaseNetworkable __instance, EntityState __state) => PopEntity(PhaseKill, __instance, __state);

            // Same never-throw rules as TimingPrefix: on any error the measurement is dropped
            private static void Push(int phase, object owner, out EntityState state)
            {
                state = default;
                try
                {
                    if (!Active) return;
                    if (_entityDepth < 0 || _entityDepth > MaxDepth) _entityDepth = 0;
                    if (_entityDepth >= MaxDepth || System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId) return;

                    // The same entity's Spawn/Kill already being timed (an override calling base): count it once
                    if (owner != null && _entityDepth > 0 && EntityPhases[_entityDepth - 1] == phase &&
                        ReferenceEquals(EntityOwners[_entityDepth - 1], owner))
                        return;

                    EntityChildTicks[_entityDepth] = 0;
                    EntityOwners[_entityDepth] = owner;
                    EntityPhases[_entityDepth] = phase;
                    state.Depth = _entityDepth;
                    state.Start = Stopwatch.GetTimestamp();
                    _entityDepth++;
                }
                catch
                {
                    _entityDepth = 0;
                    state = default;
                }
            }

            private static void PopEntity(int phase, BaseNetworkable entity, EntityState state)
            {
                if (state.Start == 0) return;
                try
                {
                    long elapsed = Stopwatch.GetTimestamp() - state.Start;
                    _entityDepth = state.Depth;
                    EntityOwners[state.Depth] = null;
                    long self = Math.Max(0, elapsed - EntityChildTicks[state.Depth]);
                    if (state.Depth > 0) EntityChildTicks[state.Depth - 1] += elapsed;
                    if (!Active) return;

                    // Reference check, not Unity's ==: a killed entity still has its prefab name
                    string name = (object)entity == null ? "unknown" : entity.ShortPrefabName ?? "unknown";
                    double ms = elapsed * TickToMs, selfMs = self * TickToMs;

                    var costs = EntityCosts[phase];
                    if (!costs.TryGetValue(name, out var cost))
                        costs[name] = cost = new EntityCost();
                    cost.Count++;
                    cost.TotalMs += ms;
                    cost.SelfMs += selfMs;
                    if (ms > cost.WorstMs) cost.WorstMs = ms;

                    FrameEntityMs[name] = (FrameEntityMs.TryGetValue(name, out var frameMs) ? frameMs : 0) + selfMs;
                    _instrumentedCalls++;
                }
                catch
                {
                    _entityDepth = 0;
                }
            }

            public static string EntityCostReport(int count, string filter)
            {
                if (_entityPatched == 0)
                    return "Entity cost timing is off (TimeEntityCosts in the config) or could not be patched";

                count = Mathf.Clamp(count, 1, 60);
                float minutes = Math.Max(1f / 60f, (Time.realtimeSinceStartup - _statsSince) / 60f);

                var selfTotals = new Dictionary<string, double>();
                foreach (var costs in EntityCosts)
                    foreach (var kv in costs)
                        selfTotals[kv.Key] = (selfTotals.TryGetValue(kv.Key, out var s) ? s : 0) + kv.Value.SelfMs;

                var sb = new StringBuilder();
                sb.AppendLine($"Entity create/spawn/kill time over {minutes:0.0} min: {selfTotals.Values.Sum() / minutes:0.0}ms/min in total");
                sb.AppendLine("Per prefab: ms/min (self) | per phase: calls/min, average ms including nested entities " +
                              "(an NPC's gear), (self = without them), worst single call");

                var rows = selfTotals
                    .Where(kv => filter.Length == 0 || kv.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(kv => kv.Value)
                    .Take(count)
                    .ToList();
                if (rows.Count == 0)
                    return sb.Append(filter.Length > 0 ? $"  nothing matching '{filter}'" : "  nothing recorded yet").ToString();

                foreach (var row in rows)
                {
                    sb.Append($"  {row.Key}: {row.Value / minutes:0.0}ms/min");
                    for (int phase = 0; phase < PhaseCount; phase++)
                    {
                        if (!EntityCosts[phase].TryGetValue(row.Key, out var c) || c.Count == 0) continue;
                        sb.Append($" | {PhaseNames[phase]} {c.Count / minutes:0.0}/min {c.TotalMs / c.Count:0.00}ms");
                        if (c.TotalMs - c.SelfMs > 0.005 * c.Count) sb.Append($" (self {c.SelfMs / c.Count:0.00})");
                        sb.Append($" worst {c.WorstMs:0.0}");
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }

            #endregion

            #region Frame accounting

            // Returns true if a garbage collection happened during this frame
            public static bool EndFrame(double frameMs)
            {
                if (!Active || _faulted) return false;
                try
                {
                    return EndFrameInner(frameMs);
                }
                catch (Exception e)
                {
                    _faulted = true;
                    _instance?.PrintError("PerfProbe frame accounting disabled after error: " + e);
                    return false;
                }
            }

            private static bool EndFrameInner(double frameMs)
            {
                _frames++;
                if (frameMs > _worstFrameMs) _worstFrameMs = frameMs;

                int second = (int)Time.realtimeSinceStartup;
                if (second != _curSecond)
                {
                    if (_curSecond >= 0)
                    {
                        SecFrames[_curSecond % 60] = _secFrames;
                        SecWorst[_curSecond % 60] = _secWorst;
                    }
                    _curSecond = second;
                    _secFrames = 0;
                    _secWorst = 0;
                    NetProbe.RollSecond();
                }
                _secFrames++;
                if (frameMs > _secWorst) _secWorst = frameMs;

                // Mono's collector is not generational: every collection counts in all generations
                int g0 = GC.CollectionCount(0);
                bool gc = g0 != _g0;
                _g0 = g0;

                bool spike = frameMs >= ThresholdMs;
                foreach (var stat in Touched)
                {
                    if (!Aggregates.TryGetValue(stat.Key, out var agg))
                        Aggregates[stat.Key] = agg = new Agg();
                    double ms = stat.Ticks * TickToMs;
                    agg.TotalMs += ms;
                    agg.Calls += stat.Calls;
                    if (ms > agg.WorstMs) agg.WorstMs = ms;
                    if (spike)
                    {
                        agg.SpikeMs += ms;
                        agg.SpikeHits++;
                    }
                }

                if (spike)
                    RecordSpike(frameMs, gc);

                foreach (var stat in Touched)
                {
                    stat.Ticks = 0;
                    stat.SelfTicks = 0;
                    stat.Calls = 0;
                }
                Touched.Clear();
                if (_frameSpawnTotal > 0) FrameSpawns.Clear();
                if (_frameKillTotal > 0) FrameKills.Clear();
                if (FrameEntityMs.Count > 0) FrameEntityMs.Clear();
                _frameSpawnTotal = _frameKillTotal = 0;
                return gc;
            }

            private static void RecordSpike(double frameMs, bool gc)
            {
                _spikeCount++;
                _spikeMsTotal += frameMs;
                Timeline.CountSpike();

                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append($" frame {frameMs:0}ms");
                if (gc)
                    sb.Append(" | GC g0+1 g1+1 g2+1");
                sb.Append($" | heap {GC.GetTotalMemory(false) / 1048576}MB");

                var top = Touched
                    .OrderByDescending(stat => stat.Ticks)
                    .Take(8)
                    .Select(stat => $"{stat.Key} {stat.Ticks * TickToMs:0.0}ms/{stat.Calls}")
                    .ToList();
                sb.Append(" | time: ").Append(top.Count == 0 ? "(nothing attributed)" : string.Join(", ", top));

                if (_frameSpawnTotal > 0)
                {
                    sb.Append($" | spawned {_frameSpawnTotal}: ");
                    sb.Append(string.Join(", ", FrameSpawns.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value}")));
                    foreach (var kv in FrameSpawns)
                        SpikeSpawnTotals[kv.Key] = SpikeSpawnTotals.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;
                }
                if (_frameKillTotal > 0)
                {
                    sb.Append($" | killed {_frameKillTotal}: ");
                    sb.Append(string.Join(", ", FrameKills.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value}")));
                }
                if (FrameEntityMs.Count > 0)
                {
                    sb.Append(" | entity time (self): ");
                    sb.Append(string.Join(", ", FrameEntityMs.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value:0.0}ms")));
                }

                string line = sb.ToString();
                Spikes.Add(line);
                if (Spikes.Count > _maxSpikes)
                    Spikes.RemoveAt(0);

                if (_instance != null)
                {
                    _instance.LogToFile("spikes", line, _instance);
                    if (_printSpikes && Time.realtimeSinceStartup - _lastPrint > 2f)
                    {
                        _lastPrint = Time.realtimeSinceStartup;
                        _instance.Puts("SPIKE " + line);
                    }
                }
            }

            #endregion

            #region Reports

            public static string Status()
            {
                int frames = 0, spikeSeconds = 0, seconds = 0;
                double worst = 0;
                for (int i = 0; i < 60; i++)
                {
                    if (SecFrames[i] == 0) continue;
                    seconds++;
                    frames += SecFrames[i];
                    if (SecWorst[i] > worst) worst = SecWorst[i];
                    if (SecWorst[i] >= ThresholdMs) spikeSeconds++;
                }

                float minutes = Math.Max(1f / 60f, (Time.realtimeSinceStartup - _statsSince) / 60f);
                var sb = new StringBuilder();
                sb.AppendLine($"PerfProbe {(Active ? "active" : "inactive")}{(_faulted ? " (FAULTED - see console)" : string.Empty)}, threshold {ThresholdMs:0}ms");
                sb.AppendLine($"Since reset ({minutes:0.0} min): {_frames:n0} frames, {_spikeCount} spikes ({_spikeCount / minutes:0.0}/min), worst frame {_worstFrameMs:0}ms");
                sb.AppendLine($"Last {seconds}s: avg {(seconds > 0 ? frames / (double)seconds : 0):0} fps, worst frame {worst:0}ms, {spikeSeconds} second(s) contained a spike");
                sb.AppendLine($"Watching {Watched.Count} method(s) ({_workQueues} work queues), plugin timers {(_timerMethod != null ? "timed" : "NOT timed")}, hooks timed for {HookPatchedCount} plugin(s), " +
                              $"entity costs {(_entityPatched > 0 ? "timed" : "NOT timed")}, console commands {(_consoleMethod != null ? "timed" : "NOT timed")}, " +
                              $"network {(NetProbe.Patched ? "counted" : "NOT counted")}, client reports {(Clients.Active ? "on" : "off")}");
                double callsPerSecond = _instrumentedCalls / Math.Max(1.0, minutes * 60.0);
                double netPerSecond = NetProbe.CallsPerSecond();
                sb.AppendLine($"Own overhead: {callsPerSecond:0} timed calls/s (~{callsPerSecond * 0.3 / 1000.0:0.00}ms/s at ~0.3us each)" +
                              (NetProbe.Patched ? $", {netPerSecond:0} packet sends/s counted (~{netPerSecond * 0.15 / 1000.0:0.00}ms/s at ~0.15us each)" : string.Empty));
                sb.Append($"GC collections: {GC.CollectionCount(0)}; heap {GC.GetTotalMemory(false) / 1048576}MB");
                return sb.ToString();
            }

            public static string RecentSpikes(int count)
            {
                if (Spikes.Count == 0) return "No spikes recorded yet";
                count = Mathf.Clamp(count, 1, 50);
                return string.Join("\n", Spikes.Skip(Math.Max(0, Spikes.Count - count)));
            }

            public static string Top(int count)
            {
                count = Mathf.Clamp(count, 1, 60);
                float minutes = Math.Max(1f / 60f, (Time.realtimeSinceStartup - _statsSince) / 60f);
                var sb = new StringBuilder();
                sb.AppendLine($"{_spikeCount} spike frame(s) totalling {_spikeMsTotal:0}ms over {minutes:0.0} min (avg {(_spikeCount > 0 ? _spikeMsTotal / _spikeCount : 0):0}ms each)");

                sb.AppendLine("Time inside spike frames (inclusive; nested entries overlap):");
                foreach (var kv in Aggregates.Where(kv => kv.Value.SpikeHits > 0).OrderByDescending(kv => kv.Value.SpikeMs).Take(count))
                    sb.AppendLine($"  {kv.Key}: {kv.Value.SpikeMs:0}ms in {kv.Value.SpikeHits} spike(s), worst single frame {kv.Value.WorstMs:0}ms");

                sb.AppendLine("Overall cost per minute (all frames):");
                foreach (var kv in Aggregates.OrderByDescending(kv => kv.Value.TotalMs).Take(count))
                    sb.AppendLine($"  {kv.Key}: {kv.Value.TotalMs / minutes:0.0}ms/min, {kv.Value.Calls / minutes:0} calls/min, worst frame {kv.Value.WorstMs:0}ms");

                if (SpikeSpawnTotals.Count > 0)
                {
                    sb.AppendLine("Entities spawned during spike frames:");
                    foreach (var kv in SpikeSpawnTotals.OrderByDescending(kv => kv.Value).Take(count))
                        sb.AppendLine($"  {kv.Key}: {kv.Value}");
                }
                return sb.ToString();
            }

            public static string EntityCensus(int count)
            {
                count = Mathf.Clamp(count, 1, 100);
                var owned = new Dictionary<string, int>();
                var unowned = new Dictionary<string, int>();
                int total = 0, totalOwned = 0;

                foreach (var networkable in BaseNetworkable.serverEntities)
                {
                    if (networkable == null) continue;
                    total++;
                    string name = networkable.ShortPrefabName ?? "unknown";
                    bool isOwned = networkable is BaseEntity entity && entity.OwnerID != 0;
                    var bucket = isOwned ? owned : unowned;
                    bucket[name] = bucket.TryGetValue(name, out var c) ? c + 1 : 1;
                    if (isOwned) totalOwned++;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"{total:n0} entities: {totalOwned:n0} player-owned, {total - totalOwned:n0} unowned");
                foreach (var name in owned.Keys.Union(unowned.Keys)
                             .OrderByDescending(n => (owned.TryGetValue(n, out var oc) ? oc : 0) + (unowned.TryGetValue(n, out var uc) ? uc : 0))
                             .Take(count))
                {
                    owned.TryGetValue(name, out var o);
                    unowned.TryGetValue(name, out var u);
                    sb.AppendLine($"  {name}: {o + u:n0} (owned {o:n0}, unowned {u:n0})");
                }
                return sb.ToString();
            }

            public static string SpawnChurn(int count)
            {
                count = Mathf.Clamp(count, 1, 60);
                float minutes = Math.Max(1f / 60f, (Time.realtimeSinceStartup - _statsSince) / 60f);
                var sb = new StringBuilder();
                sb.AppendLine($"Entity churn over {minutes:0.0} min: {SpawnTotals.Values.Sum():n0} spawned, {KillTotals.Values.Sum():n0} killed");
                sb.AppendLine("Top spawns (per minute, killed per minute):");
                foreach (var kv in SpawnTotals.OrderByDescending(kv => kv.Value).Take(count))
                {
                    KillTotals.TryGetValue(kv.Key, out var killed);
                    sb.AppendLine($"  {kv.Key}: {kv.Value / minutes:0.0}/min spawned, {killed / minutes:0.0}/min killed");
                }
                return sb.ToString();
            }

            #endregion
        }

        #endregion


        #region Engine probes

        // 0.4.0. Everything above times things with names: hooks, timers, invokes, watched methods. These three probes
        // reach the rest of the frame.
        //   Loop: the Unity player loop (PlayerLoop API) with a stopwatch delegate inserted before and after every
        //         system. It attributes engine work no script owns: physics, navmesh obstacle carving (AIUpdatePostScript),
        //         animation constraints (ConstraintManagerUpdate), and the script phases as a whole.
        //   Behaviours: a Harmony stopwatch on every Update/LateUpdate/FixedUpdate declared by any MonoBehaviour, to name
        //         the script behind a busy script phase.
        //   Components: live components of a Unity type, grouped by the entity prefab and object they sit on, so a busy
        //         engine system can be traced to what feeds it (10,500 NavMeshObstacles on loot barrels; 180
        //         RotationConstraints on horse hoof pads, in the first use).
        // A one-second loop snapshot runs every LoopSnapshotMinutes and is kept for perfprobe.status and loop-<day>.csv.
        private static class Engine
        {
            private static PerfProbe _plugin;
            private static PluginConfig _cfg;

            public static void Init(PerfProbe plugin, PluginConfig config)
            {
                _plugin = plugin; _cfg = config;
                if (config.LoopSnapshotMinutes > 0)
                {
                    _snapshotTimer = plugin.timer.Every(config.LoopSnapshotMinutes * 60f, () => LoopStart(1, config.LoopSnapshotTop, manual: false));
                    plugin.timer.Once(20f, () => LoopStart(1, config.LoopSnapshotTop, manual: false));   // a first look soon after load
                }
            }

            // perfprobe.detach: stop timers and restore the player loop; the detacher removes the Harmony patches
            public static void StopForDetach()
            {
                _snapshotTimer?.Destroy(); _snapshotTimer = null;
                _loopTimer?.Destroy(); _loopTimer = null;
                if (_loopActive) LoopRestore();
                _behTimer?.Destroy(); _behTimer = null;
            }

            public static void Shutdown()
            {
                _snapshotTimer?.Destroy(); _snapshotTimer = null;
                _loopTimer?.Destroy(); _loopTimer = null;
                if (_loopActive) LoopRestore();
                _behTimer?.Destroy(); _behTimer = null;
                if (_behHarmony != null) BehavioursStop(0, false);
                // the next instance appends to the same day's file: a writer left open made its first write a sharing violation
                try { _loopWriter?.Dispose(); } catch { }
                _loopWriter = null;
                _plugin = null;
            }

            #region Player loop

            private class LoopMarker { }
            private class LoopStat { public string Name; public int Depth; public Stopwatch Sw = new Stopwatch(); public double TotalMs, MaxMs; public int Calls, Over20; }
            private static List<LoopStat> _loopStats;
            private static PlayerLoopSystem _loopOrig;
            private static bool _loopActive, _loopManual;
            private static float _loopStart;
            private static Timer _loopTimer, _snapshotTimer;

            // last snapshot (manual or periodic), for status and perfprobe.loop with no arguments
            public class LoopLine { public string Name; public double MsPerSecond, MaxMs; public int Over20; }
            private static readonly List<LoopLine> _lastSnapshot = new List<LoopLine>();
            private static DateTime _lastSnapshotAt;
            private static float _lastSnapshotSeconds;

            public static string LoopStart(int seconds, int top, bool manual)
            {
                if (_loopActive) return manual ? "Loop probe already running" : string.Empty;
                if (_plugin == null) return "PerfProbe not initialised";
                _loopStats = new List<LoopStat>();
                try
                {
                    _loopOrig = PlayerLoop.GetCurrentPlayerLoop();
                    PlayerLoop.SetPlayerLoop(LoopWrap(_loopOrig, string.Empty, 0));
                }
                catch (Exception e) { return "Loop probe failed to install: " + e.Message; }
                _loopActive = true; _loopManual = manual; _loopStart = Time.realtimeSinceStartup;
                _loopTimer = _plugin.timer.Once(seconds, () => LoopReport(top));
                return $"Loop probe: timing {_loopStats.Count} player-loop systems for {seconds}s; the result prints to the console";
            }

            private static PlayerLoopSystem LoopWrap(PlayerLoopSystem sys, string prefix, int depth)
            {
                var copy = sys;
                if (sys.subSystemList == null || sys.subSystemList.Length == 0) return copy;
                var list = new List<PlayerLoopSystem>(sys.subSystemList.Length * 3);
                foreach (var child in sys.subSystemList)
                {
                    string name = prefix + (child.type != null ? child.type.Name : "?");
                    var st = new LoopStat { Name = name, Depth = depth };
                    _loopStats.Add(st);
                    list.Add(new PlayerLoopSystem { type = typeof(LoopMarker), updateDelegate = () => st.Sw.Restart() });
                    list.Add(child.subSystemList != null && child.subSystemList.Length > 0 ? LoopWrap(child, name + ".", depth + 1) : child);
                    list.Add(new PlayerLoopSystem { type = typeof(LoopMarker), updateDelegate = () =>
                    {
                        st.Sw.Stop();
                        double ms = st.Sw.Elapsed.TotalMilliseconds;
                        st.TotalMs += ms; st.Calls++;
                        if (ms > st.MaxMs) st.MaxMs = ms;
                        if (ms > 20) st.Over20++;
                    } });
                }
                copy.subSystemList = list.ToArray();
                return copy;
            }

            private static void LoopRestore()
            {
                try { PlayerLoop.SetPlayerLoop(_loopOrig); }
                catch (Exception e) { _plugin?.PrintError("Restoring the player loop failed: " + e.Message); }
                _loopActive = false;
            }

            private static void LoopReport(int top)
            {
                if (!_loopActive) return;
                float secs = Math.Max(0.001f, Time.realtimeSinceStartup - _loopStart);
                LoopRestore();
                _loopTimer = null;

                _lastSnapshot.Clear();
                foreach (var st in _loopStats.Where(x => x.Depth > 0 && x.Calls > 0).OrderByDescending(x => x.TotalMs).Take(Math.Max(top, _cfg.LoopSnapshotTop)))
                    _lastSnapshot.Add(new LoopLine { Name = st.Name, MsPerSecond = st.TotalMs / secs, MaxMs = st.MaxMs, Over20 = st.Over20 });
                _lastSnapshotAt = DateTime.UtcNow; _lastSnapshotSeconds = secs;
                WriteLoopCsv();

                if (!_loopManual) return;
                var sb = new StringBuilder($"[PerfProbe] player loop over {secs:0}s, ms per second of wall time (a phase contains its children):\n");
                foreach (var st in _loopStats.Where(x => x.Depth == 0))
                    sb.Append($"  {st.TotalMs / secs,8:F1} ms/s {st.Calls / secs,7:F0}/s  max {st.MaxMs,6:F1} ms  >20ms {st.Over20,4}  {st.Name}\n");
                sb.Append("  -- systems by total:\n");
                foreach (var st in _loopStats.Where(x => x.Depth > 0).OrderByDescending(x => x.TotalMs).Take(top))
                    sb.Append($"  {st.TotalMs / secs,8:F1} ms/s {st.Calls / secs,7:F0}/s  max {st.MaxMs,6:F1} ms  >20ms {st.Over20,4}  {st.Name}\n");
                sb.Append("  -- systems by worst single call:\n");
                foreach (var st in _loopStats.Where(x => x.Depth > 0 && x.MaxMs >= 5).OrderByDescending(x => x.MaxMs).Take(10))
                    sb.Append($"  max {st.MaxMs,6:F1} ms  >20ms {st.Over20,4}  {st.TotalMs / secs,8:F1} ms/s  {st.Name}\n");
                foreach (var hint in Hints(_lastSnapshot)) sb.Append("  => " + hint + "\n");
                _plugin.Puts(sb.ToString().TrimEnd());
            }

            public static string LastSnapshotText()
            {
                if (_lastSnapshot.Count == 0) return "No loop snapshot yet. perfprobe.loop <seconds> runs one now" + (_cfg != null && _cfg.LoopSnapshotMinutes > 0 ? $"; one is taken every {_cfg.LoopSnapshotMinutes} min" : string.Empty);
                var sb = new StringBuilder($"Player loop snapshot at {_lastSnapshotAt:HH:mm:ss} UTC ({_lastSnapshotSeconds:0}s), ms per second:\n");
                foreach (var l in _lastSnapshot) sb.Append($"  {l.MsPerSecond,8:F1} ms/s  max {l.MaxMs,6:F1} ms  {l.Name}\n");
                foreach (var hint in Hints(_lastSnapshot)) sb.Append("  => " + hint + "\n");
                return sb.ToString().TrimEnd();
            }

            // For perfprobe.status: the top five of the last snapshot and what to run next
            public static string StatusSuffix()
            {
                if (_lastSnapshot.Count == 0) return string.Empty;
                var sb = new StringBuilder($"\nPlayer loop ({_lastSnapshotAt:HH:mm} UTC): ");
                sb.Append(string.Join(", ", _lastSnapshot.Take(5).Select(l => $"{ShortName(l.Name)} {l.MsPerSecond:0}ms/s")));
                foreach (var hint in Hints(_lastSnapshot)) sb.Append("\n  => " + hint);
                return sb.ToString();
            }

            private static string ShortName(string name) { int i = name.LastIndexOf('.'); return i >= 0 ? name.Substring(i + 1) : name; }

            // Which engine systems point at which components. Thresholds are ms per second of main-thread time.
            private struct Hint { public string System; public double MinMsPerSecond; public string Text; }
            private static readonly Hint[] HintTable =
            {
                new Hint { System = "AIUpdatePostScript", MinMsPerSecond = 20, Text = "Unity navmesh work (obstacle carving, crowd): perfprobe.components NavMeshObstacle NavMeshAgent. On a -useNewNavmesh server the obstacles carve a navmesh that is never built; NavCompat 0.2.0 switches them off" },
                new Hint { System = "ConstraintManagerUpdate", MinMsPerSecond = 10, Text = "Unity animation constraints: perfprobe.components RotationConstraint ParentConstraint PositionConstraint AimConstraint LookAtConstraint ScaleConstraint. A server never reads a visual rig; ServerTrim switches them off by prefab" },
                new Hint { System = "ScriptRunBehaviourLateUpdate", MinMsPerSecond = 60, Text = "script LateUpdate: perfprobe.behaviours 30 names the scripts" },
                new Hint { System = "PhysicsFixedUpdate", MinMsPerSecond = 150, Text = "PhysX step: physics.print_colliders_per_prefab shows what the colliders are" },
                new Hint { System = "ScriptRunDelayedDynamicFrameRate", MinMsPerSecond = 30, Text = "coroutines (yield null / WaitForSeconds): no profiler names them; look for plugin coroutines doing bulk work" },
                new Hint { System = "DirectorUpdate", MinMsPerSecond = 10, Text = "Unity Timeline/Playable directors: perfprobe.components PlayableDirector" },
                new Hint { System = "LegacyAnimationUpdate", MinMsPerSecond = 10, Text = "legacy Animation components: perfprobe.components Animation" },
            };

            private static IEnumerable<string> Hints(List<LoopLine> lines)
            {
                foreach (var h in HintTable)
                {
                    var l = lines.FirstOrDefault(x => x.Name.EndsWith(h.System, StringComparison.Ordinal));
                    if (l != null && l.MsPerSecond >= h.MinMsPerSecond) yield return $"{h.System} {l.MsPerSecond:0} ms/s: {h.Text}";
                }
                var upd = lines.FirstOrDefault(x => x.Name.EndsWith("ScriptRunBehaviourUpdate", StringComparison.Ordinal));
                if (upd != null && upd.Over20 > 0) yield return $"ScriptRunBehaviourUpdate had {upd.Over20} call(s) over 20 ms: a script Update spikes; perfprobe.behaviours 30 names it, perfprobe.spikes shows what else was in those frames";
            }

            private static StreamWriter _loopWriter; private static string _loopDay;

            private static void WriteLoopCsv()
            {
                string dir = Timeline.Dir;
                if (dir == null || _lastSnapshot.Count == 0) return;
                try
                {
                    string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
                    if (_loopWriter == null || day != _loopDay)
                    {
                        try { _loopWriter?.Dispose(); } catch { }
                        string path = Path.Combine(dir, $"loop-{day}.csv");
                        bool exists = File.Exists(path);
                        _loopWriter = new StreamWriter(path, true, new UTF8Encoding(false), 16 * 1024);
                        if (!exists) _loopWriter.WriteLine("time,seconds,system,ms_per_s,max_ms,over20ms");
                        _loopDay = day;
                    }
                    var ci = CultureInfo.InvariantCulture;
                    long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (var l in _lastSnapshot.Take(_cfg.LoopSnapshotTop))
                        _loopWriter.WriteLine(string.Join(",", t.ToString(ci), _lastSnapshotSeconds.ToString("0", ci), l.Name, l.MsPerSecond.ToString("0.00", ci), l.MaxMs.ToString("0.00", ci), l.Over20.ToString(ci)));
                    _loopWriter.Flush();
                }
                catch (Exception e)
                {
                    try { _loopWriter?.Dispose(); } catch { }
                    _loopWriter = null;
                    _plugin?.PrintWarning("Loop CSV disabled after write error: " + e.Message);
                }
            }

            #endregion

            #region Behaviours

            private class BehStat { public string Name, Kind; public long Ticks, MaxTicks; public int Calls, Over20; }
            private static readonly Dictionary<MethodBase, BehStat> _beh = new Dictionary<MethodBase, BehStat>();
            private static Harmony _behHarmony; private static Timer _behTimer; private static float _behStart; private static int _behFrame0, _behFrames;
            private static readonly string[] BehNames = { "Update", "LateUpdate", "FixedUpdate" };
            private static readonly List<string> _behFailed = new List<string>();
            private const string BehHarmonyId = HarmonyId + ".behaviours";

            public static string BehavioursStart(int seconds, int top, string filter)
            {
                if (_behHarmony != null) return "Behaviour probe already running";
                if (_plugin == null) return "PerfProbe not initialised";
                _beh.Clear(); _behFailed.Clear();
                _behHarmony = new Harmony(BehHarmonyId);
                var harmony = _behHarmony;
                var pre = new HarmonyMethod(typeof(Engine), nameof(BehPrefix));
                var post = new HarmonyMethod(typeof(Engine), nameof(BehPostfix));
                // 0.5.1: collected here, patched over several frames (patching ~300 methods in one frame was an 830 ms frame)
                var work = new List<(MethodInfo Method, BehStat Stat)>();
                var seen = new HashSet<MethodInfo>();
                int types = 0;
                var sw = Stopwatch.StartNew();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] all;
                    try { all = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }
                    foreach (var t in all)
                    {
                        // every check is guarded: a type whose dependencies are missing throws from IsAssignableFrom (MySqlX on Carbon).
                        // Abstract bases are included: FacepunchBehaviour-style bases declare the Update their subclasses run.
                        bool isMb;
                        try { isMb = t != null && !t.IsGenericTypeDefinition && typeof(MonoBehaviour).IsAssignableFrom(t); } catch { continue; }
                        if (!isMb) continue;
                        if (filter.Length > 0 && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        bool any = false;
                        foreach (var name in BehNames)
                        {
                            MethodInfo m;
                            try
                            {
                                m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                                // 0.4.1: a method declared on a generic base (Base<T>.LateUpdate) is never declared on any
                                // non-generic type, so it was skipped; take it from the closed base the type inherits
                                if (m == null)
                                {
                                    var inh = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                                    // Harmony needs the method as declared on the closed base, not as reflected through the subclass.
                                    // This is how the invoke scheduler (InvokeHandlerBase<T>.LateUpdate, which runs every Invoke
                                    // callback on the server) gets timed.
                                    if (inh != null && inh.DeclaringType != null && inh.DeclaringType.IsGenericType && !inh.DeclaringType.IsGenericTypeDefinition)
                                        m = inh.DeclaringType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                                }
                                if (m == null || m.IsAbstract || m.GetMethodBody() == null || !seen.Add(m)) continue;
                            }
                            catch { continue; }
                            work.Add((m, new BehStat { Name = (m.DeclaringType != t ? m.DeclaringType.Name + " via " : string.Empty) + t.FullName + "." + name, Kind = name }));
                            any = true;
                        }
                        if (any) types++;
                    }
                }
                int patched = 0, failed = 0;
                var steps = work.Select(w => (Action)(() =>
                {
                    if (_behHarmony != harmony) return;
                    try { harmony.Patch(w.Method, pre, post); _beh[w.Method] = w.Stat; patched++; }
                    catch (Exception e) { failed++; if (_behFailed.Count < 20) _behFailed.Add($"{w.Stat.Name} ({e.GetType().Name}: {e.Message.Split('\n')[0]})"); }
                })).ToList();
                bool started = Calls.Budgeted(steps, () =>
                {
                    if (_behHarmony != harmony || _plugin == null) return;
                    foreach (var st in _beh.Values) { st.Ticks = st.MaxTicks = 0; st.Calls = st.Over20 = 0; }
                    _behStart = Time.realtimeSinceStartup; _behFrame0 = Time.frameCount;
                    _behTimer = _plugin.timer.Once(seconds, () => BehavioursStop(top, true));
                    _plugin.Puts($"[PerfProbe] behaviour probe: {patched} methods patched ({failed} failed{(_behFailed.Count > 0 ? ": " + string.Join("; ", _behFailed) : string.Empty)}); timing for {seconds}s");
                });
                if (!started) { _behHarmony = null; return "PerfProbe not initialised"; }
                return $"Behaviour probe: found {work.Count} methods on {types} MonoBehaviour types in {sw.ElapsedMilliseconds}ms; patching them over the next few seconds " +
                       $"(at most 8 ms per frame), then timing for {seconds}s; progress and the result print to the console";
            }

            private static void BehPrefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

            private static void BehPostfix(long __state, MethodBase __originalMethod)
            {
                BehStat st;
                if (!_beh.TryGetValue(__originalMethod, out st)) return;
                long t = Stopwatch.GetTimestamp() - __state;
                st.Ticks += t; st.Calls++;
                if (t > st.MaxTicks) st.MaxTicks = t;
                if (t > Stopwatch.Frequency / 50) st.Over20++;
            }

            private static void BehavioursStop(int top, bool report)
            {
                float secs = Math.Max(0.001f, Time.realtimeSinceStartup - _behStart);
                _behFrames = Time.frameCount - _behFrame0;
                _behTimer = null;
                var harmony = _behHarmony;
                // Unload: all at once. After a report: over several frames, keeping _behHarmony set until done so a new
                // run cannot start against half-removed patches
                if (!report || _plugin == null || !Calls.Budgeted(_beh.Keys.Select(m => (Action)(() => harmony?.Unpatch(m, HarmonyPatchType.All, BehHarmonyId))).ToList(),
                        () => { if (_behHarmony == harmony) _behHarmony = null; }))
                {
                    try { harmony?.UnpatchAll(BehHarmonyId); } catch (Exception e) { _plugin?.PrintError("Behaviour probe unpatch failed: " + e.Message); }
                    _behHarmony = null;
                }
                if (!report || _plugin == null) return;
                double ms = 1000.0 / Stopwatch.Frequency;
                var sb = new StringBuilder($"[PerfProbe] script Update/LateUpdate/FixedUpdate over {secs:0}s, {_beh.Count} methods timed:\n");
                // 0.4.1: totals per kind. The loop probe's script phase minus this total is Unity's per-call dispatch
                // (native to managed, once per component per frame), which no method timer can see: many cheap
                // components show up as a large phase with nothing large inside it.
                float fps = Math.Max(1f, _behFrames / secs);
                foreach (var kind in BehNames)
                {
                    var ofKind = _beh.Values.Where(x => x.Kind == kind && x.Calls > 0).ToList();
                    if (ofKind.Count == 0) continue;
                    double kms = ofKind.Sum(x => x.Ticks) * ms / secs; double kcalls = ofKind.Sum(x => (double)x.Calls) / secs;
                    double perFrame = kind == "FixedUpdate" ? kcalls / 16.0 : kcalls / fps;
                    sb.Append($"  {kind}: {ofKind.Count} methods, {kms:F1} ms/s inside them, {kcalls:F0} calls/s (~{perFrame:F0} components per {(kind == "FixedUpdate" ? "fixed step" : "frame")})\n");
                }
                foreach (var st in _beh.Values.Where(x => x.Calls > 0).OrderByDescending(x => x.Ticks).Take(top))
                    sb.Append($"  {st.Ticks * ms / secs,8:F2} ms/s {st.Calls / secs,7:F0}/s  max {st.MaxTicks * ms,6:F1} ms  >20ms {st.Over20,4}  {st.Name}\n");
                sb.Append("  -- most called (many cheap components cost dispatch time outside the methods):\n");
                foreach (var st in _beh.Values.Where(x => x.Calls > 0).OrderByDescending(x => x.Calls).Take(Math.Min(top, 12)))
                    sb.Append($"  {st.Calls / secs,8:F0}/s (~{st.Calls / secs / fps:F0} per frame)  {st.Ticks * ms / secs,7:F2} ms/s  {st.Name}\n");
                sb.Append("  -- by worst single call:\n");
                foreach (var st in _beh.Values.Where(x => x.MaxTicks * ms >= 5).OrderByDescending(x => x.MaxTicks).Take(10))
                    sb.Append($"  max {st.MaxTicks * ms,6:F1} ms  >20ms {st.Over20,4}  {st.Ticks * ms / secs,8:F2} ms/s  {st.Name}\n");
                _plugin.Puts(sb.ToString().TrimEnd());
            }

            #endregion

            #region Components

            // Counts live components of a type (FindObjectsByType incl. inactive, 1-40 ms per type on a big scene),
            // with enabled count, carving (NavMeshObstacle), and the prefabs and objects that carry them, plus a
            // collider flag on the object, so a trim can be judged safe.
            public static string Components(List<string> names)
            {
                var sb = new StringBuilder();
                foreach (var name in names)
                {
                    Type type = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { type = asm.GetTypes().FirstOrDefault(t => t.Name == name || t.FullName == name); } catch { }
                        if (type != null) break;
                    }
                    if (type == null) { sb.Append($"{name}: type not found\n"); continue; }
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) { sb.Append($"{type.FullName}: not a Unity object type\n"); continue; }
                    var sw = Stopwatch.StartNew();
                    UnityEngine.Object[] objs;
                    try { objs = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None); }
                    catch (Exception e) { sb.Append($"{type.FullName}: {e.Message}\n"); continue; }
                    int enabled = 0, carving = 0, activeGo = 0;
                    var carveProp = type.GetProperty("carving");
                    var owners = new Dictionary<string, int>();
                    var objects = new Dictionary<string, int>();
                    foreach (var o in objs)
                    {
                        var b = o as Behaviour; if (b != null && b.enabled) enabled++;
                        var c = o as Component;
                        if (c != null)
                        {
                            if (c.gameObject.activeInHierarchy) activeGo++;
                            var ent = c.GetComponentInParent<BaseEntity>();
                            string owner = ent != null ? ent.ShortPrefabName : c.transform.root.name;
                            int n; owners.TryGetValue(owner, out n); owners[owner] = n + 1;
                            string oname = c.gameObject.name + (c.GetComponent<Collider>() != null ? " [collider]" : string.Empty);
                            int m; objects.TryGetValue(oname, out m); objects[oname] = m + 1;
                        }
                        if (carveProp != null) { try { if ((bool)carveProp.GetValue(o)) carving++; } catch { } }
                    }
                    sb.Append($"{type.FullName}: {objs.Length} total, {enabled} enabled, {activeGo} on active objects{(carveProp != null ? $", {carving} carving" : string.Empty)} ({sw.ElapsedMilliseconds}ms to count)\n");
                    if (owners.Count > 0) sb.Append("  prefabs: " + string.Join(", ", owners.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}")) + "\n");
                    if (objects.Count > 0) sb.Append("  objects: " + string.Join(", ", objects.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}")) + "\n");
                }
                return sb.ToString().TrimEnd();
            }

            #endregion
        }

        #endregion

        #region Detach

        // 0.5.3. Unload has to remove every patch at once (the plugin's code is about to go), ~4 s on a big server. Detach
        // does the same work beforehand, a few patches per frame, so the unload that follows has nothing left to remove.
        private static class Detacher
        {
            public static bool Running;

            public static string Start(PerfProbe plugin, Harmony harmony)
            {
                Probe.Active = false;
                Calls.StopForDetach();
                Engine.StopForDetach();
                Clients.Shutdown();
                Timeline.Shutdown();

                var ids = new HashSet<string> { HarmonyId, HarmonyId + ".invokes", HarmonyId + ".behaviours" };
                var work = new List<(MethodBase Method, string Id)>();
                foreach (var method in Harmony.GetAllPatchedMethods().ToList())
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    foreach (var owner in info.Owners)
                        if (ids.Contains(owner)) work.Add((method, owner));
                }

                var steps = work.Select(w => (Action)(() => harmony.Unpatch(w.Method, HarmonyPatchType.All, w.Id))).ToList();
                Running = true;
                bool started = Calls.Budgeted(steps, () =>
                {
                    Running = false;
                    plugin.Puts("PerfProbe detached, fast unload can now be performed");
                });
                if (!started)
                {
                    foreach (var step in steps) { try { step(); } catch { } }
                    Running = false;
                    return "PerfProbe detached, fast unload can now be performed";
                }
                return $"Detaching: recording stopped, removing {work.Count} patch(es) over the next few seconds (at most 8 ms per frame); " +
                       "'PerfProbe detached, fast unload can now be performed' prints when done";
            }
        }

        #endregion

        #region Call windows

        // 0.5.0. Entity invokes (every Invoke/InvokeRepeating callback, run by the invoke scheduler) are too frequent to time
        // permanently, so like perfprobe.behaviours they are patched for a window and unpatched after; while the window
        // records, they also feed spikes, top and the timeline through the same self-time stack as everything else.
        // 0.5.1: server RPCs are timed permanently through their one dispatch point, BaseEntity.SV_RPCMessage. 0.5.0 patched
        // each [RPC_Server] method (638 on a live server) for a window, which cost 60-100 ms frames while patching and a
        // 530 ms collection of the ~1 GB Harmony allocated.
        private static class Calls
        {
            private const double PatchBudgetMs = 8;
            private const int MaxPrefabsPerStat = 200;
            private const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private static readonly long Over20Ticks = Stopwatch.Frequency / 50;

            private static PerfProbe _plugin;
            private static MonoBehaviour _host;
            private static int _invReplaced;

            private class PrefabStat { public long Ticks; public int Calls; }

            private class CallStat
            {
                public string Key;
                public bool Match = true;
                public long Ticks, SelfTicks, MaxTicks;
                public int Calls, Over20;
                public Dictionary<string, PrefabStat> Prefabs;
            }

            private class Window
            {
                public string Kind, Id;
                public int Category;
                public string State = "idle"; // idle, finding, patching, recording, unpatching
                public bool Recording;
                public Harmony Harmony;
                public Timer Timer;
                public Coroutine Job;
                public string Filter = string.Empty;
                public int Seconds, Top;
                public float Start;
                public readonly List<MethodBase> Found = new List<MethodBase>();
                public readonly List<MethodBase> Patched = new List<MethodBase>();
                public readonly Dictionary<MethodBase, CallStat> Stats = new Dictionary<MethodBase, CallStat>();
                public int Failed;
                public string LastError, LastReport;
            }

            private static readonly Window Inv = new Window { Kind = "invokes", Id = HarmonyId + ".invokes", Category = Cat.Invokes };

            private static MethodBase _rpcMethod;
            private static readonly Dictionary<uint, CallStat> RpcStats = new Dictionary<uint, CallStat>();
            private static float _rpcSince;

            public static bool RpcsActive => _rpcMethod != null;

            // Runs the steps a few per frame (at most PatchBudgetMs of them per frame), then done. False if not initialised.
            public static bool Budgeted(IList<Action> steps, Action done)
            {
                if (_host == null) return false;
                _host.StartCoroutine(BudgetedRun(steps, done));
                return true;
            }

            private static System.Collections.IEnumerator BudgetedRun(IList<Action> steps, Action done)
            {
                var sw = Stopwatch.StartNew();
                foreach (var step in steps)
                {
                    if (sw.Elapsed.TotalMilliseconds >= PatchBudgetMs)
                    {
                        yield return null;
                        sw.Restart();
                    }
                    try { step(); }
                    catch (Exception e) { _plugin?.PrintWarning("Patch step failed: " + e.Message); }
                }
                try { done?.Invoke(); }
                catch (Exception e) { _plugin?.PrintWarning("Patch completion failed: " + e.Message); }
            }

            public static void Init(PerfProbe plugin, MonoBehaviour host)
            {
                _plugin = plugin;
                _host = host;
            }

            // perfprobe.detach: stop the invoke window without unpatching (the detacher removes every patch)
            public static void StopForDetach()
            {
                Inv.Recording = false;
                Inv.Timer?.Destroy();
                Inv.Timer = null;
                try { if (Inv.Job != null && _host != null) _host.StopCoroutine(Inv.Job); } catch { }
                Inv.Job = null;
                Inv.State = "idle";
            }

            public static void Shutdown()
            {
                _rpcMethod = null;
                RpcStats.Clear();
                foreach (var w in new[] { Inv })
                {
                    w.Recording = false;
                    w.Timer?.Destroy();
                    w.Timer = null;
                    try { if (w.Job != null && _host != null) _host.StopCoroutine(w.Job); } catch { }
                    w.Job = null;
                    try { w.Harmony?.UnpatchAll(w.Id); }
                    catch (Exception e) { _plugin?.PrintError($"{w.Kind} probe unpatch failed: {e.Message}"); }
                    w.Harmony = null;
                    w.Patched.Clear();
                    w.Found.Clear();
                    w.State = "idle";
                }
                _plugin = null;
                _host = null;
            }

            public static string InvokesStart(int seconds, int top, string filter)
            {
                string busy = Busy(Inv);
                if (busy != null) return busy;
                Inv.Found.Clear();
                string error = FindInvokeTicks(Inv.Found);
                if (Inv.Found.Count == 0) return "Invoke probe unavailable: " + error;
                _invReplaced = 0;
                Begin(Inv, seconds, top, filter, null);
                return $"Invoke probe: timing every Invoke/InvokeRepeating callback{FilterText(filter)} for {seconds}s " +
                       $"(the invoke scheduler is patched for the window); the result prints to the console";
            }

            public static string LastReport(bool invokes)
            {
                var w = Inv;
                string state = w.State == "idle" ? string.Empty : Busy(w) + "\n";
                return state + (w.LastReport ?? "No invokes result yet. perfprobe.invokes <seconds> [top] [filter] runs one");
            }

            public static string StatusLine()
            {
                string invokes = Inv.State == "recording"
                    ? $"recording, {Math.Max(0, Inv.Seconds - (Time.realtimeSinceStartup - Inv.Start)):0}s left"
                    : Inv.State;
                string rpcs = RpcsActive ? $"timed ({RpcStats.Values.Sum(s => (long)s.Calls):n0} calls since reset)" : "NOT timed";
                return $"\nInvoke window: {invokes} (perfprobe.invokes); RPCs {rpcs} (perfprobe.rpcs)";
            }

            #region RPCs

            public static string PatchRpcs(Harmony harmony)
            {
                var method = typeof(BaseEntity).GetMethods(AllDeclared).FirstOrDefault(m =>
                    m.Name == "SV_RPCMessage" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(uint));
                if (method == null) return "RPC timing unavailable: BaseEntity.SV_RPCMessage(uint, Message) not found";
                try
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(Calls), nameof(RpcPrefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(Calls), nameof(RpcFinalizer)));
                }
                catch (Exception e)
                {
                    return "RPC timing failed: " + e.Message;
                }
                _rpcMethod = method;
                _rpcSince = Time.realtimeSinceStartup;
                return "Timing every server RPC via BaseEntity.SV_RPCMessage";
            }

            public static void ResetRpcs()
            {
                RpcStats.Clear();
                _rpcSince = Time.realtimeSinceStartup;
            }

            // Harmony shares __state only between patches declared in the same class, so the prefix lives here too
            public static void RpcPrefix(out Probe.CallState __state) => Probe.TimingPrefix(out __state);

            public static void RpcFinalizer(BaseEntity __instance, uint __0, Probe.CallState __state)
            {
                if (__state.Start == 0) return;
                try
                {
                    long elapsed = Probe.Pop(__state, out long self);
                    if (!Probe.Active) return;
                    if (!RpcStats.TryGetValue(__0, out var stat))
                        RpcStats[__0] = stat = new CallStat { Key = "rpc:" + RpcName(__0) };
                    Add(stat, elapsed, self, __instance);
                    Probe.Record(stat.Key, Cat.Rpcs, elapsed, self);
                }
                catch
                {
                    Probe.ResetDepth();
                }
            }

            private static string RpcName(uint id)
            {
                try { return StringPool.Get(id) ?? id.ToString(CultureInfo.InvariantCulture); }
                catch { return id.ToString(CultureInfo.InvariantCulture); }
            }

            public static string RpcReport(int count, string filter)
            {
                if (!RpcsActive) return "RPC timing is off (TimeRpcs in the config) or could not be patched";
                float secs = Math.Max(1f, Time.realtimeSinceStartup - _rpcSince);
                var stats = RpcStats.Values.Where(s => s.Calls > 0 &&
                                                       (filter.Length == 0 || s.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                return BuildReport($"server RPCs since reset ({secs / 60f:0.0} min)", stats, secs, Mathf.Clamp(count, 5, 100), filter, Cat.Rpcs, false);
            }

            #endregion

            private static string Busy(Window w)
            {
                if (_plugin == null || _host == null) return "PerfProbe not initialised";
                if (w.State == "idle") return null;
                if (w.State == "recording")
                    return $"{w.Kind} probe already running, {Math.Max(0, w.Seconds - (Time.realtimeSinceStartup - w.Start)):0}s left";
                return $"{w.Kind} probe busy ({w.State})";
            }

            private static string FilterText(string filter) => string.IsNullOrEmpty(filter) ? string.Empty : $" matching '{filter}'";

            private static void Begin(Window w, int seconds, int top, string filter, System.Collections.IEnumerator find)
            {
                w.Seconds = seconds;
                w.Top = Mathf.Clamp(top, 5, 100);
                w.Filter = filter ?? string.Empty;
                w.Stats.Clear();
                w.Patched.Clear();
                w.Failed = 0;
                w.LastError = null;
                w.Harmony = new Harmony(w.Id);
                w.State = "finding";
                w.Job = _host.StartCoroutine(Run(w, find));
            }

            private static System.Collections.IEnumerator Run(Window w, System.Collections.IEnumerator find)
            {
                string failure = null;
                if (find != null)
                {
                    while (true)
                    {
                        bool more;
                        try { more = find.MoveNext(); }
                        catch (Exception e) { failure = "search failed: " + e.Message; break; }
                        if (!more) break;
                        yield return null;
                    }
                }
                if (failure == null && w.Found.Count == 0)
                    failure = "nothing to patch" + FilterText(w.Filter);

                if (failure == null)
                {
                    w.State = "patching";
                    bool isInvoke = w == Inv;
                    var transpiler = new HarmonyMethod(typeof(Calls), nameof(InvokeTranspiler));
                    var sw = Stopwatch.StartNew();
                    foreach (var method in w.Found)
                    {
                        if (sw.Elapsed.TotalMilliseconds >= PatchBudgetMs)
                        {
                            yield return null;
                            sw.Restart();
                        }
                        try
                        {
                            w.Harmony.Patch(method, transpiler: transpiler);
                            w.Patched.Add(method);
                        }
                        catch (Exception e)
                        {
                            w.Failed++;
                            w.LastError = $"{method.DeclaringType?.Name}.{method.Name}: {e.Message.Split('\n')[0]}";
                        }
                    }
                    if (w.Patched.Count == 0) failure = "patching failed: " + w.LastError;
                    else if (isInvoke && _invReplaced == 0) failure = "no Action.Invoke call found in the invoke scheduler (a Rust update changed it)";
                }

                if (_plugin == null) yield break;
                if (failure != null)
                {
                    try { w.Harmony.UnpatchAll(w.Id); } catch { }
                    w.Harmony = null;
                    w.Patched.Clear();
                    w.Job = null;
                    w.State = "idle";
                    w.LastReport = $"[PerfProbe] {w.Kind} probe: {failure}";
                    _plugin.Puts(w.LastReport);
                    yield break;
                }

                w.Start = Time.realtimeSinceStartup;
                w.Recording = true;
                w.State = "recording";
                w.Job = null;
                w.Timer = _plugin.timer.Once(w.Seconds, () => Stop(w));
                _plugin.Puts($"[PerfProbe] {w.Kind} probe: {w.Patched.Count} method(s) patched" +
                             (w.Failed > 0 ? $", {w.Failed} failed (last: {w.LastError})" : string.Empty) + $"; timing for {w.Seconds}s");
            }

            private static void Stop(Window w)
            {
                if (w.State != "recording") return;
                w.Recording = false;
                w.Timer = null;
                float secs = Math.Max(0.001f, Time.realtimeSinceStartup - w.Start);
                w.LastReport = BuildReport(w, secs);
                _plugin?.Puts(w.LastReport);
                w.State = "unpatching";
                if (_host != null) w.Job = _host.StartCoroutine(Unpatch(w));
            }

            private static System.Collections.IEnumerator Unpatch(Window w)
            {
                var sw = Stopwatch.StartNew();
                foreach (var method in w.Patched)
                {
                    if (sw.Elapsed.TotalMilliseconds >= PatchBudgetMs)
                    {
                        yield return null;
                        sw.Restart();
                    }
                    try { w.Harmony.Unpatch(method, HarmonyPatchType.All, w.Id); }
                    catch (Exception e) { _plugin?.PrintWarning($"{w.Kind} probe: unpatching {method.Name} failed: {e.Message}"); }
                }
                w.Patched.Clear();
                w.Harmony = null;
                w.Job = null;
                w.State = "idle";
            }

            #region Invokes

            // The scheduler is InvokeHandlerBase<T>.DoTick; every closed base in its assembly is patched
            private static string FindInvokeTicks(List<MethodBase> into)
            {
                var handler = AccessTools.TypeByName("InvokeHandler");
                if (handler == null) return "InvokeHandler not found";
                Type generic = null;
                for (var b = handler.BaseType; b != null && generic == null; b = b.BaseType)
                    if (b.IsGenericType && b.Name.StartsWith("InvokeHandlerBase", StringComparison.Ordinal))
                        generic = b.GetGenericTypeDefinition();
                if (generic == null) return "InvokeHandlerBase<T> not found";

                Type[] types;
                try { types = handler.Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in types)
                {
                    var b = t?.BaseType;
                    if (b == null || !b.IsGenericType || b.ContainsGenericParameters || b.GetGenericTypeDefinition() != generic) continue;
                    foreach (var tick in b.GetMethods(AllDeclared).Where(m => m.Name == "DoTick" && !m.IsAbstract))
                        if (!into.Contains(tick)) into.Add(tick);
                }
                return into.Count == 0 ? "InvokeHandlerBase<T>.DoTick not found" : null;
            }

            // Replaces the callback's Action.Invoke() inside DoTick with InvokeWrapper(action): same stack shape, no
            // dependency on the InvokeAction type (the approach Rust.ServerMetrics uses, applied for a window only)
            public static IEnumerable<CodeInstruction> InvokeTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                var invoke = AccessTools.Method(typeof(Action), nameof(Action.Invoke));
                var wrapper = AccessTools.Method(typeof(Calls), nameof(InvokeWrapper));
                foreach (var ci in instructions)
                {
                    if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo m && m == invoke)
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = wrapper;
                        _invReplaced++;
                    }
                    yield return ci;
                }
            }

            public static void InvokeWrapper(Action action)
            {
                if (!Inv.Recording)
                {
                    action();
                    return;
                }
                Probe.TimingPrefix(out var state);
                try
                {
                    action();
                }
                finally
                {
                    InvokeDone(action, state);
                }
            }

            // Never throws (same rules as Probe.TimingPrefix): on any error the measurement is dropped
            private static void InvokeDone(Action action, Probe.CallState state)
            {
                if (state.Start == 0) return;
                try
                {
                    long elapsed = Probe.Pop(state, out long self);
                    if (!Inv.Recording || !Probe.Active) return;
                    var method = action.Method;
                    if (!Inv.Stats.TryGetValue(method, out var stat))
                    {
                        string key = InvokeKey(method);
                        Inv.Stats[method] = stat = new CallStat
                        {
                            Key = key,
                            Match = Inv.Filter.Length == 0 || key.IndexOf(Inv.Filter, StringComparison.OrdinalIgnoreCase) >= 0
                        };
                    }
                    if (!stat.Match) return;
                    Add(stat, elapsed, self, action.Target);
                    Probe.Record(stat.Key, Cat.Invokes, elapsed, self);
                }
                catch
                {
                    Probe.ResetDepth();
                }
            }

            // Lambdas live in compiler-generated closure classes: name them after the class that declared the lambda
            private static string InvokeKey(MethodInfo method)
            {
                var type = method.DeclaringType;
                if (type == null) return "invoke:" + method.Name;
                if (type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null)
                    return $"invoke:{type.DeclaringType.Name}.{method.Name}";
                return $"invoke:{type.Name}.{method.Name}";
            }

            #endregion

            private static void Add(CallStat stat, long elapsed, long self, object target)
            {
                stat.Ticks += elapsed;
                stat.SelfTicks += self;
                stat.Calls++;
                if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;
                if (elapsed > Over20Ticks) stat.Over20++;

                var entity = target as BaseNetworkable;
                if ((object)entity == null) return;
                string prefab = entity.ShortPrefabName ?? "unknown";
                if (stat.Prefabs == null) stat.Prefabs = new Dictionary<string, PrefabStat>();
                if (!stat.Prefabs.TryGetValue(prefab, out var p))
                {
                    if (stat.Prefabs.Count >= MaxPrefabsPerStat) return;
                    stat.Prefabs[prefab] = p = new PrefabStat();
                }
                p.Ticks += elapsed;
                p.Calls++;
            }

            private static string BuildReport(Window w, float secs) =>
                BuildReport($"Invoke/InvokeRepeating callbacks over {secs:0}s", w.Stats.Values.Where(s => s.Calls > 0).ToList(), secs, w.Top, w.Filter, w.Category, true);

            private static string BuildReport(string what, List<CallStat> stats, float secs, int top, string filter, int category, bool window)
            {
                double ms = Probe.TickToMs;
                double totalMs = stats.Sum(s => (double)s.Ticks) * ms, selfMs = stats.Sum(s => (double)s.SelfTicks) * ms;
                double calls = stats.Sum(s => (double)s.Calls);

                var sb = new StringBuilder();
                sb.Append($"[PerfProbe] {what}{FilterText(filter)}: {stats.Count} distinct, {calls / secs:n0} calls/s, " +
                          $"{totalMs / secs:F1} ms/s ({selfMs / secs:F1} ms/s self, without timed calls nested inside); " +
                          $"probe cost ~{calls / secs * 0.3 / 1000.0:F1} ms/s\n");
                if (stats.Count == 0) return sb.Append("  nothing was called").ToString();

                foreach (var s in stats.OrderByDescending(x => x.Ticks).Take(top))
                {
                    sb.Append($"  {s.Ticks * ms / secs,8:F2} ms/s {s.Calls / secs,8:F0}/s  avg {s.Ticks * ms / s.Calls,6:F3} ms  max {s.MaxTicks * ms,6:F1} ms  >20ms {s.Over20,3}  {s.Key}");
                    if (s.SelfTicks < s.Ticks * 0.9) sb.Append($"  (self {s.SelfTicks * ms / secs:F2} ms/s)");
                    sb.Append('\n');
                    if (s.Prefabs != null && s.Prefabs.Count > 0)
                        sb.Append("           on: " + string.Join(", ", s.Prefabs.OrderByDescending(kv => kv.Value.Ticks).Take(4)
                                      .Select(kv => $"{kv.Key} {kv.Value.Ticks * ms / secs:F2}ms/s {kv.Value.Calls / secs:F0}/s")) +
                                  (s.Prefabs.Count > 4 ? $" (+{s.Prefabs.Count - 4} more prefabs)" : string.Empty) + "\n");
                }

                sb.Append("  -- most called:\n");
                foreach (var s in stats.OrderByDescending(x => x.Calls).Take(Math.Min(top, 12)))
                    sb.Append($"  {s.Calls / secs,8:F0}/s  {s.Ticks * ms / secs,8:F2} ms/s  {s.Key}\n");

                var worst = stats.Where(x => x.MaxTicks * ms >= 5).OrderByDescending(x => x.MaxTicks).Take(10).ToList();
                if (worst.Count > 0)
                {
                    sb.Append("  -- by worst single call:\n");
                    foreach (var s in worst)
                        sb.Append($"  max {s.MaxTicks * ms,6:F1} ms  >20ms {s.Over20,3}  {s.Key}\n");
                }
                sb.Append(window
                    ? $"  (during the window these also fed perfprobe.spikes, perfprobe.top and the timeline's {Cat.Names[category]}_ms column)"
                    : $"  (these also feed perfprobe.spikes, perfprobe.top and the timeline's {Cat.Names[category]}_ms column)");
                return sb.ToString();
            }
        }

        #endregion

        #region Network

        // 0.5.0. Packets and bytes sent per message type, counted once per recipient (NetWrite.PacketID remembers the
        // type, NetWrite.Send counts it), plus the engine's own totals. Net.sv's stat and ping methods are looked up by
        // reflection so a renamed member costs the column, not the plugin.
        private static class NetProbe
        {
            public static bool Patched;
            private static int _lastType;
            private static Func<object, long> _length;
            private static readonly string[] Names = new string[256];
            private static readonly long[] Packets = new long[256], Bytes = new long[256];             // since reset
            private static readonly long[] SecPackets = new long[256], SecBytes = new long[256];       // second in progress
            private static readonly long[] LastPackets = new long[256], LastBytes = new long[256];     // last full second
            private static long _calls, _secCalls;
            private static float _since;

            private static bool _lookedUp;
            private static MethodInfo _getStat, _getPing;
            private static Type _statEnum;
            private static readonly Dictionary<string, object> StatIds = new Dictionary<string, object>();

            public static string Patch(Harmony harmony)
            {
                Patched = false;
                var writeType = AccessTools.TypeByName("Network.NetWrite");
                if (writeType == null) return "Network counters unavailable: Network.NetWrite not found";
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var packetId = writeType.GetMethods(flags).FirstOrDefault(m => m.Name == "PacketID" && m.GetParameters().Length == 1 &&
                                                                              m.GetParameters()[0].ParameterType == typeof(global::Network.Message.Type));
                var send = writeType.GetMethods(flags).FirstOrDefault(m => m.Name == "Send" && m.GetParameters().Length == 1 &&
                                                                          m.GetParameters()[0].ParameterType == typeof(global::Network.SendInfo));
                if (packetId == null || send == null)
                    return $"Network counters unavailable: NetWrite.{(packetId == null ? "PacketID(Message.Type)" : "Send(SendInfo)")} not found";

                _length = LengthReader(writeType);
                var typeEnum = typeof(global::Network.Message.Type);
                for (int i = 0; i < 256; i++)
                {
                    string name = null;
                    try { name = Enum.GetName(typeEnum, Enum.ToObject(typeEnum, i)); } catch { }
                    Names[i] = name ?? "type" + i;
                }

                try
                {
                    harmony.Patch(packetId, postfix: new HarmonyMethod(typeof(NetProbe), nameof(PacketIdPostfix)));
                    harmony.Patch(send, prefix: new HarmonyMethod(typeof(NetProbe), nameof(SendPrefix)));
                }
                catch (Exception e)
                {
                    try { harmony.Unpatch(packetId, HarmonyPatchType.All, HarmonyId); } catch { }
                    return "Network counters failed: " + e.Message;
                }
                Patched = true;
                Reset();
                return "Counting packets sent per message type via NetWrite.PacketID/Send" + (_length == null ? " (packet sizes unavailable)" : string.Empty);
            }

            private static Func<object, long> LengthReader(Type type)
            {
                try
                {
                    if (typeof(Stream).IsAssignableFrom(type)) return o => ((Stream)o).Length;
                    var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                    var cast = System.Linq.Expressions.Expression.Convert(param, type);
                    System.Linq.Expressions.Expression member = null;
                    var prop = AccessTools.Property(type, "Length");
                    if (prop != null && prop.GetIndexParameters().Length == 0 && prop.GetGetMethod(true) != null)
                        member = System.Linq.Expressions.Expression.Property(cast, prop);
                    else
                    {
                        var field = AccessTools.Field(type, "Length") ?? AccessTools.Field(type, "length") ?? AccessTools.Field(type, "_length");
                        if (field != null) member = System.Linq.Expressions.Expression.Field(cast, field);
                    }
                    if (member == null) return null;
                    return System.Linq.Expressions.Expression.Lambda<Func<object, long>>(
                        System.Linq.Expressions.Expression.Convert(member, typeof(long)), param).Compile();
                }
                catch
                {
                    return null;
                }
            }

            public static void PacketIdPostfix(global::Network.Message.Type __0) => _lastType = (int)__0 & 255;

            public static void SendPrefix(object __instance, global::Network.SendInfo __0)
            {
                try
                {
                    _secCalls++;
                    int recipients = __0.connection != null ? 1 : __0.connections != null ? __0.connections.Count : 0;
                    if (recipients == 0) return;
                    int type = _lastType;
                    SecPackets[type] += recipients;
                    if (_length != null) SecBytes[type] += _length(__instance) * recipients;
                }
                catch
                {
                    // counting only; never disturb a send
                }
            }

            // Called once per second by the frame accounting
            public static void RollSecond()
            {
                if (!Patched) return;
                for (int i = 0; i < 256; i++)
                {
                    LastPackets[i] = SecPackets[i];
                    LastBytes[i] = SecBytes[i];
                    Packets[i] += SecPackets[i];
                    Bytes[i] += SecBytes[i];
                    SecPackets[i] = 0;
                    SecBytes[i] = 0;
                }
                _calls += _secCalls;
                _secCalls = 0;
            }

            public static void Reset()
            {
                Array.Clear(Packets, 0, Packets.Length);
                Array.Clear(Bytes, 0, Bytes.Length);
                _calls = 0;
                _since = Time.realtimeSinceStartup;
            }

            public static long LastSecondPackets() => Patched ? LastPackets.Sum() : 0;

            public static double CallsPerSecond() => _calls / Math.Max(1.0, Time.realtimeSinceStartup - _since);

            // Engine stat by its BaseNetwork.StatTypeLong name; connection null = server-wide. -1 when unavailable.
            public static long Stat(object connection, string name)
            {
                try
                {
                    object sv = global::Network.Net.sv;
                    if (sv == null) return -1;
                    LookUp(sv);
                    if (_getStat == null) return -1;
                    if (!StatIds.TryGetValue(name, out var id))
                    {
                        try { id = Enum.Parse(_statEnum, name); } catch { id = null; }
                        StatIds[name] = id;
                    }
                    if (id == null) return -1;
                    return Convert.ToInt64(_getStat.Invoke(sv, new[] { connection, id }), CultureInfo.InvariantCulture);
                }
                catch
                {
                    return -1;
                }
            }

            public static double Ping(object connection)
            {
                if (connection == null) return -1;
                try
                {
                    object sv = global::Network.Net.sv;
                    if (sv == null) return -1;
                    LookUp(sv);
                    if (_getPing == null) return -1;
                    return Convert.ToDouble(_getPing.Invoke(sv, new[] { connection }), CultureInfo.InvariantCulture);
                }
                catch
                {
                    return -1;
                }
            }

            private static void LookUp(object sv)
            {
                if (_lookedUp) return;
                _lookedUp = true;
                var methods = sv.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _getStat = methods.FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return m.Name == "GetStat" && ps.Length == 2 && ps[1].ParameterType.IsEnum && ps[1].ParameterType.Name == "StatTypeLong";
                });
                _statEnum = _getStat?.GetParameters()[1].ParameterType;
                _getPing = methods.FirstOrDefault(m => m.Name == "GetAveragePing" && m.GetParameters().Length == 1);
            }

            public static string StatusLine()
            {
                long inBytes = Stat(null, "BytesReceived_LastSecond"), outBytes = Stat(null, "BytesSent_LastSecond");
                string engine = inBytes < 0 && outBytes < 0
                    ? "engine totals unavailable"
                    : $"in {inBytes / 1024.0:0} KB/s, out {outBytes / 1024.0:0} KB/s, packet loss {Stat(null, "PacketLossLastSecond")}";
                return $"Network (last second): {engine}" +
                       (Patched ? $"; {LastSecondPackets():n0} packets sent to players (perfprobe.net)" : string.Empty);
            }

            public static string Report(int count)
            {
                if (!Patched) return "Network counters are off (TrackNetwork in the config) or could not be patched";
                count = Mathf.Clamp(count, 1, 60);
                float secs = Math.Max(1f, Time.realtimeSinceStartup - _since);
                long packets = Packets.Sum(), bytes = Bytes.Sum();
                bool sizes = _length != null;

                var sb = new StringBuilder();
                sb.AppendLine($"Network sent over {secs / 60f:0.0} min, counted once per recipient: {packets / secs:n0} packets/s" +
                              (sizes ? $", {bytes / secs / 1024:0.0} KB/s of payload" : " (packet sizes unavailable)"));
                sb.AppendLine(StatusLine());
                sb.AppendLine(sizes ? "By message type: packets/s, KB/s, average size | last second" : "By message type: packets/s | last second");
                foreach (int i in Enumerable.Range(0, 256).Where(i => Packets[i] > 0 || LastPackets[i] > 0)
                             .OrderByDescending(i => sizes ? Bytes[i] : Packets[i]).Take(count))
                {
                    sb.Append($"  {Names[i]}: {Packets[i] / secs:n0}/s");
                    if (sizes) sb.Append($", {Bytes[i] / secs / 1024:0.0} KB/s, avg {(Packets[i] > 0 ? Bytes[i] / (double)Packets[i] : 0):0} B");
                    sb.Append($" | {LastPackets[i]:n0}" + (sizes ? $" / {LastBytes[i] / 1024.0:0.0} KB" : string.Empty));
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }
        }

        #endregion

        #region Clients

        // 0.5.0, opt-in (ClientPerfReports). Every PlayerSampleSeconds each client is asked for a performance report, the
        // way Rust.ServerMetrics does it: the "legacy" request, tagged with our own request id. The reply arrives in
        // BasePlayer.PerformanceReport; a transpiler hands it to OnReport inside the "legacy" branch, and replies carrying our
        // id skip the vanilla console print. Replies to an admin's own requests are left alone.
        private static class Clients
        {
            private static readonly int RequestId = new System.Random().Next(1, int.MaxValue);

            private class Sample { public double Fps, MemoryMB; public float At; }

            private static readonly Dictionary<ulong, Sample> Latest = new Dictionary<ulong, Sample>();
            private static MethodBase _method;
            private static Timer _timer;
            private static bool _transpiled;
            private static float _every;

            public static bool Active => _method != null;

            public static string Patch(PerfProbe plugin, Harmony harmony, float everySeconds)
            {
                var method = typeof(BasePlayer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "PerformanceReport" && m.GetParameters().Length == 1);
                if (method == null) return "Client reports unavailable: BasePlayer.PerformanceReport not found";

                _transpiled = false;
                try { harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Clients), nameof(ReportTranspiler))); }
                catch (Exception e) { return "Client reports failed: " + e.Message; }
                if (!_transpiled)
                {
                    try { harmony.Unpatch(method, HarmonyPatchType.Transpiler, HarmonyId); } catch { }
                    return "Client reports unavailable: BasePlayer.PerformanceReport has changed (no \"legacy\" branch with a report to hook)";
                }

                _method = method;
                _every = Math.Max(5f, everySeconds);
                _timer = plugin.timer.Every(_every, RequestAll);
                return $"Asking clients for fps/memory every {_every:0}s via BasePlayer.PerformanceReport";
            }

            public static void Shutdown()
            {
                _timer?.Destroy();
                _timer = null;
                _method = null;
                Latest.Clear();
            }

            private static void RequestAll()
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    try
                    {
                        if (player == null || !player.IsConnected || player.IsReceivingSnapshot) continue;
                        player.ClientRPC(RpcTarget.Player("GetPerformanceReport", player), "legacy", RequestId);
                    }
                    catch
                    {
                        // one client failing must not stop the others
                    }
                }
                if (Latest.Count > BasePlayer.activePlayerList.Count * 2 + 50)
                {
                    var online = new HashSet<ulong>(BasePlayer.activePlayerList.Where(p => p != null).Select(p => (ulong)p.userID));
                    foreach (var id in Latest.Keys.Where(id => !online.Contains(id)).ToList()) Latest.Remove(id);
                }
            }

            // After `if (format == "legacy")` inserts: if (Clients.OnReport(this, report)) return;
            public static IEnumerable<CodeInstruction> ReportTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
            {
                var list = instructions.ToList();
                try
                {
                    var body = original.GetMethodBody();
                    if (body == null || body.ExceptionHandlingClauses.Count > 0) return list;
                    var report = body.LocalVariables.FirstOrDefault(l => l.LocalType.Name.IndexOf("PerformanceReport", StringComparison.Ordinal) >= 0);
                    if (report == null || report.LocalType.IsValueType) return list;
                    foreach (var field in new[] { "request_id", "fps", "memory_system" })
                        if (!Reflect.Has(report.LocalType, field)) return list;

                    int idx = list.FindIndex(ci => ci.opcode == OpCodes.Ldstr && ci.operand as string == "legacy");
                    if (idx < 0 || idx + 2 >= list.Count) return list;
                    if (!(list[idx + 1].operand is MethodInfo eq && eq.Name == "op_Equality")) return list;
                    var branch = list[idx + 2];
                    if (branch.opcode != OpCodes.Brfalse && branch.opcode != OpCodes.Brfalse_S) return list;
                    var load = list.FirstOrDefault(ci => LocalIndex(ci) == report.LocalIndex);
                    var last = list[list.Count - 1];
                    if (load == null || last.opcode != OpCodes.Ret) return list;

                    var end = generator.DefineLabel();
                    last.labels.Add(end);
                    list.InsertRange(idx + 3, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(load.opcode, load.operand),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Clients), nameof(OnReport))),
                        new CodeInstruction(OpCodes.Brtrue, end)
                    });
                    _transpiled = true;
                    return list;
                }
                catch
                {
                    return list;
                }
            }

            private static int LocalIndex(CodeInstruction ci)
            {
                if (ci.opcode == OpCodes.Ldloc_0) return 0;
                if (ci.opcode == OpCodes.Ldloc_1) return 1;
                if (ci.opcode == OpCodes.Ldloc_2) return 2;
                if (ci.opcode == OpCodes.Ldloc_3) return 3;
                if (ci.opcode != OpCodes.Ldloc && ci.opcode != OpCodes.Ldloc_S) return -1;
                if (ci.operand is LocalBuilder builder) return builder.LocalIndex;
                if (ci.operand is int i) return i;
                if (ci.operand is byte b) return b;
                if (ci.operand is short s) return s;
                return -1;
            }

            public static bool OnReport(BasePlayer player, object report)
            {
                try
                {
                    if ((object)player == null || report == null || (int)Reflect.Num(report, "request_id", 0) != RequestId) return false;
                    Latest[player.userID] = new Sample
                    {
                        Fps = Reflect.Num(report, "fps"),
                        MemoryMB = Reflect.Num(report, "memory_system", 0),
                        At = Time.realtimeSinceStartup
                    };
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public static bool TryGet(ulong id, float maxAge, out double fps, out double memoryMB)
            {
                fps = -1;
                memoryMB = 0;
                if (!Latest.TryGetValue(id, out var s) || Time.realtimeSinceStartup - s.At > maxAge) return false;
                fps = s.Fps;
                memoryMB = s.MemoryMB;
                return true;
            }

            public static string Report(int count, string sort)
            {
                count = Mathf.Clamp(count, 1, 200);
                float now = Time.realtimeSinceStartup;
                var rows = new List<(string Name, double Ping, long Loss, double Fps, double Mem, float Age)>();
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null) continue;
                    var connection = p.net?.connection;
                    double fps = -1, mem = 0;
                    float age = -1;
                    if (Latest.TryGetValue(p.userID, out var s))
                    {
                        fps = s.Fps;
                        mem = s.MemoryMB;
                        age = now - s.At;
                    }
                    rows.Add((p.displayName ?? p.UserIDString, NetProbe.Ping(connection), NetProbe.Stat(connection, "PacketLossLastSecond"), fps, mem, age));
                }
                if (rows.Count == 0) return "No players online";

                if (sort == "fps") rows = rows.OrderBy(r => r.Fps < 0 ? double.MaxValue : r.Fps).ToList();
                else if (sort == "loss") rows = rows.OrderByDescending(r => r.Loss).ThenByDescending(r => r.Ping).ToList();
                else rows = rows.OrderByDescending(r => r.Ping).ToList();

                var withFps = rows.Where(r => r.Fps >= 0).Select(r => r.Fps).ToList();
                var sb = new StringBuilder();
                sb.Append($"{rows.Count} player(s) online, median ping {Median(rows.Where(r => r.Ping >= 0).Select(r => r.Ping).ToList()):0}ms, " +
                          $"{rows.Count(r => r.Loss > 0)} with packet loss in the last second");
                sb.AppendLine(Active
                    ? $"; client reports every {_every:0}s: {withFps.Count} reporting, median {Median(withFps):0} fps, {withFps.Count(f => f < 30)} under 30 fps"
                    : "; client fps/memory off (ClientPerfReports in the config)");
                foreach (var r in rows.Take(count))
                {
                    string name = r.Name.Length > 20 ? r.Name.Substring(0, 20) : r.Name;
                    sb.Append($"  {name,-20} ping {r.Ping,4:0}ms  loss {r.Loss,3}");
                    if (r.Fps >= 0) sb.Append($"  client {r.Fps,3:0} fps {r.Mem,6:0}MB ({r.Age:0}s ago)");
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }

            private static double Median(List<double> values)
            {
                if (values.Count == 0) return 0;
                values.Sort();
                return values[values.Count / 2];
            }
        }

        #endregion

        #region Live counters

        // Scheduler and connection-queue counters read on demand. Members outside the usual plugin surface are read by
        // reflection (cached), so a rename costs a number, not the plugin.
        private static class Live
        {
            public static float EngineKB(string stat)
            {
                long bytes = NetProbe.Stat(null, stat);
                return bytes < 0 ? 0 : bytes / 1024f;
            }

            private static object ConnectionQueue()
            {
                var mgr = ServerMgr.Instance;
                return mgr == null ? null : Reflect.Get(mgr, "connectionQueue");
            }

            public static long Joining() => (long)Reflect.Num(ConnectionQueue(), "Joining");

            public static long Queued() => (long)Reflect.Num(ConnectionQueue(), "Queued");

            public static string Status()
            {
                var perf = Performance.current;
                return $"Scheduler: {perf.invokeHandlerTasks} invoke tasks, {perf.loadBalancerTasks} load balancer tasks, " +
                       $"workshop skin queue {Reflect.Num(perf, "workshopSkinsQueued", 0):0}; connection queue {Joining()} joining, {Queued()} queued\n" +
                       NetProbe.StatusLine();
            }
        }

        // Cached property/field readers by type and name, for the few members read by reflection
        private static class Reflect
        {
            private static readonly Dictionary<Type, Dictionary<string, Func<object, object>>> Getters =
                new Dictionary<Type, Dictionary<string, Func<object, object>>>();

            public static object Get(object obj, string name)
            {
                if (obj == null) return null;
                try
                {
                    var type = obj.GetType();
                    if (!Getters.TryGetValue(type, out var byName))
                        Getters[type] = byName = new Dictionary<string, Func<object, object>>();
                    if (!byName.TryGetValue(name, out var get))
                        byName[name] = get = Build(type, name);
                    return get?.Invoke(obj);
                }
                catch
                {
                    return null;
                }
            }

            public static double Num(object obj, string name, double fallback = -1)
            {
                var value = Get(obj, name);
                if (value == null) return fallback;
                try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
                catch { return fallback; }
            }

            public static bool Has(Type type, string name) => Build(type, name) != null;

            private static Func<object, object> Build(Type type, string name)
            {
                var prop = AccessTools.Property(type, name);
                if (prop != null && prop.GetIndexParameters().Length == 0 && prop.GetGetMethod(true) != null)
                    return o => prop.GetValue(o, null);
                var field = AccessTools.Field(type, name);
                if (field != null) return o => field.GetValue(o);
                return null;
            }
        }

        #endregion

        #region Timeline

        // One row per second (aligned to Rust's own Performance sampling) plus player samples every few
        // seconds. Stored in pre-allocated arrays of plain numbers: nothing is allocated per second, and
        // pointer-free arrays add nothing to the garbage collector's work.
        private static class Timeline
        {
            public const int ActBuild = 0, ActAttack = 1, ActLoot = 2, ActCraft = 3, ActGather = 4, ActCount = 5;

            private struct Row
            {
                public int Time;
                public int Frames;
                public float WorstFrameMs;
                public float Update, LateUpdate, PreLateUpdate, FixedUpdate, Physics, TotalCpu;
                public float GcPauseMs;
                public int HeapMB, Gc, Invokes, LoadBalancer, Entities, Players, Spikes, Spawned, Killed;
                // 0.5.0: engine network totals for the last second, packets PerfProbe counted, connection and skin queues
                public float NetInKB, NetOutKB;
                public int PacketLoss, PacketsOut, Joining, Queued, SkinQueue;
            }

            private struct PlayerSample
            {
                public int Time;
                public ushort Player;
                public short X, Z;
                public ushort SpeedDm; // decimetres per second
                public byte Flags;     // 1 sleeping, 2 mounted, 4 wounded, 8 dead
                public ushort Build, Attack, Loot, Craft, Gather;
                // 0.5.0: connection quality, and the client's own report (-1 / 0 when there is none)
                public ushort PingMs, PacketLoss;
                public short ClientFps;
                public ushort ClientMemMB;
            }

            private static readonly string[] RowColumns =
            {
                "time", "frames", "worst_frame_ms",
                "update_ms", "late_update_ms", "pre_late_update_ms", "fixed_update_ms", "physics_ms", "total_cpu_ms",
                "gc_pause_ms", "heap_mb", "gc", "invoke_tasks", "load_balancer_tasks", "entities", "players", "spikes", "spawned", "killed",
                "net_in_kb", "net_out_kb", "packet_loss", "packets_out", "joining", "queued", "workshop_skin_queue"
            };

            private static bool _enabled;
            private static PluginConfig _config;

            private static Row[] _rows = new Row[0];
            private static float[] _cats = new float[0];
            private static int _rowHead, _rowCount;

            private static PlayerSample[] _samples = new PlayerSample[0];
            private static int _sampleHead, _sampleCount;

            // Per-player index (stable for the session), last position and activity counters
            private static readonly Dictionary<ulong, int> PlayerIndex = new Dictionary<ulong, int>();
            private static readonly List<ulong> PlayerIds = new List<ulong>();
            private static readonly List<string> PlayerNames = new List<string>();
            private static Vector3[] _lastPos = new Vector3[64];
            private static ushort[] _activity = new ushort[64 * ActCount];

            // Accumulators for the second in progress
            private static readonly double[] SecCat = new double[Cat.Count];
            private static int _secFrames, _secSpikes, _secSpawned, _secKilled;
            private static double _secWorst, _secGcPause, _typicalFrameMs = 15;
            private static bool _secGc;
            private static int _lastPerfFrameId = -1;
            private static float _nextPlayerSample;

            // CSV output
            private static string _dir;
            public static string Dir => _dir;
            private static StreamWriter _rowWriter, _playerWriter;
            private static string _fileDay;
            private static int _unflushed;

            public static void Init(PluginConfig config)
            {
                _config = config;
                int rows = config.TimelineRetentionMinutes * 60;
                _rows = new Row[rows];
                _cats = new float[rows * Cat.Count];
                _rowHead = _rowCount = 0;

                int sampleSlots = Math.Max(1, rows / config.PlayerSampleSeconds) * config.MaxPlayersPerSample;
                _samples = new PlayerSample[sampleSlots];
                _sampleHead = _sampleCount = 0;

                Array.Clear(SecCat, 0, SecCat.Length);
                _lastPerfFrameId = -1;
                _nextPlayerSample = Time.realtimeSinceStartup + config.PlayerSampleSeconds;

                if (config.WriteCsv)
                {
                    try
                    {
                        _dir = Path.Combine(Interface.Oxide.DataDirectory, "PerfProbe");
                        Directory.CreateDirectory(_dir);
                        CompressOldFiles();
                    }
                    catch (Exception e)
                    {
                        _dir = null;
                        _instance?.PrintWarning("Timeline CSV disabled: " + e.Message);
                    }
                }

                _enabled = true;
            }

            public static void Shutdown()
            {
                _enabled = false;
                CloseWriters();
            }

            public static void AddSelfTime(int category, double ms)
            {
                if (_enabled) SecCat[category] += ms;
            }

            public static void CountSpike()
            {
                if (_enabled) _secSpikes++;
            }

            public static void CountEntity(bool spawned)
            {
                if (!_enabled) return;
                if (spawned) _secSpawned++;
                else _secKilled++;
            }

            public static void CountActivity(BasePlayer player, int activity)
            {
                if (!_enabled || player == null || player.IsNpc) return;
                int index = IndexOf(player);
                int slot = index * ActCount + activity;
                if (_activity[slot] < ushort.MaxValue) _activity[slot]++;
            }

            private static int IndexOf(BasePlayer player)
            {
                ulong id = player.userID;
                if (PlayerIndex.TryGetValue(id, out int index))
                {
                    PlayerNames[index] = player.displayName; // names can change
                    return index;
                }

                index = PlayerIds.Count;
                PlayerIndex[id] = index;
                PlayerIds.Add(id);
                PlayerNames.Add(player.displayName);
                if (index >= _lastPos.Length)
                {
                    Array.Resize(ref _lastPos, _lastPos.Length * 2);
                    Array.Resize(ref _activity, _lastPos.Length * ActCount);
                }
                _lastPos[index] = player.transform.position;
                return index;
            }

            public static void EndFrame(double frameMs, bool gc)
            {
                if (!_enabled) return;
                try
                {
                    _secFrames++;
                    if (frameMs > _secWorst) _secWorst = frameMs;
                    if (gc)
                    {
                        // The collector's pause is roughly this frame minus a normal frame
                        _secGc = true;
                        _secGcPause += Math.Max(0, frameMs - _typicalFrameMs);
                    }
                    else if (frameMs < Probe.ThresholdMs)
                    {
                        _typicalFrameMs = _typicalFrameMs * 0.98 + frameMs * 0.02;
                    }

                    // Rust's Performance component closes a 1-second sample; close ours with it
                    int perfFrame = Performance.current.frameID;
                    if (perfFrame != _lastPerfFrameId)
                    {
                        if (_lastPerfFrameId != -1)
                            CloseSecond();
                        _lastPerfFrameId = perfFrame;
                    }

                    if (Time.realtimeSinceStartup >= _nextPlayerSample)
                    {
                        _nextPlayerSample = Time.realtimeSinceStartup + _config.PlayerSampleSeconds;
                        SamplePlayers();
                    }
                }
                catch (Exception e)
                {
                    _enabled = false;
                    _instance?.PrintError("PerfProbe timeline disabled after error: " + e);
                }
            }

            private static void CloseSecond()
            {
                var perf = Performance.current;
                var sample = perf.performanceSample;

                var row = new Row
                {
                    Time = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Frames = _secFrames,
                    WorstFrameMs = (float)_secWorst,
                    Update = (float)sample.Update.TotalMilliseconds,
                    LateUpdate = (float)sample.LateUpdate.TotalMilliseconds,
                    PreLateUpdate = (float)sample.PreLateUpdate.TotalMilliseconds,
                    FixedUpdate = (float)sample.FixedUpdate.TotalMilliseconds,
                    Physics = (float)sample.PhysicsUpdate.TotalMilliseconds,
                    TotalCpu = (float)sample.TotalCPU.TotalMilliseconds,
                    GcPauseMs = (float)_secGcPause,
                    HeapMB = (int)perf.memoryAllocations,
                    Gc = _secGc ? 1 : 0,
                    Invokes = (int)perf.invokeHandlerTasks,
                    LoadBalancer = (int)perf.loadBalancerTasks,
                    Entities = BaseNetworkable.serverEntities.Count,
                    Players = BasePlayer.activePlayerList.Count,
                    Spikes = _secSpikes,
                    Spawned = _secSpawned,
                    Killed = _secKilled,
                    NetInKB = Live.EngineKB("BytesReceived_LastSecond"),
                    NetOutKB = Live.EngineKB("BytesSent_LastSecond"),
                    PacketLoss = (int)Math.Max(0, NetProbe.Stat(null, "PacketLossLastSecond")),
                    PacketsOut = (int)Math.Min(int.MaxValue, NetProbe.LastSecondPackets()),
                    Joining = (int)Math.Max(0, Live.Joining()),
                    Queued = (int)Math.Max(0, Live.Queued()),
                    SkinQueue = (int)Math.Max(0, Reflect.Num(perf, "workshopSkinsQueued"))
                };

                int slot = _rowHead;
                _rows[slot] = row;
                for (int c = 0; c < Cat.Count; c++)
                    _cats[slot * Cat.Count + c] = (float)SecCat[c];
                _rowHead = (_rowHead + 1) % _rows.Length;
                if (_rowCount < _rows.Length) _rowCount++;

                WriteRowCsv(row, slot);

                Array.Clear(SecCat, 0, SecCat.Length);
                _secFrames = _secSpikes = _secSpawned = _secKilled = 0;
                _secWorst = _secGcPause = 0;
                _secGc = false;
            }

            private static void SamplePlayers()
            {
                int time = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int written = 0;
                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || written >= _config.MaxPlayersPerSample) continue;
                    int index = IndexOf(player);
                    var pos = player.transform.position;
                    float speed = Vector3.Distance(pos, _lastPos[index]) / _config.PlayerSampleSeconds;
                    _lastPos[index] = pos;

                    byte flags = 0;
                    if (player.IsSleeping()) flags |= 1;
                    if (player.isMounted) flags |= 2;
                    if (player.IsWounded()) flags |= 4;
                    if (player.IsDead()) flags |= 8;

                    int a = index * ActCount;
                    var connection = player.net?.connection;
                    Clients.TryGet(player.userID, _config.PlayerSampleSeconds * 3f, out double clientFps, out double clientMem);
                    var sample = new PlayerSample
                    {
                        Time = time,
                        Player = (ushort)Math.Min(index, ushort.MaxValue),
                        X = (short)Mathf.Clamp(pos.x, short.MinValue, short.MaxValue),
                        Z = (short)Mathf.Clamp(pos.z, short.MinValue, short.MaxValue),
                        SpeedDm = (ushort)Mathf.Clamp(speed * 10f, 0, ushort.MaxValue),
                        Flags = flags,
                        Build = _activity[a + ActBuild],
                        Attack = _activity[a + ActAttack],
                        Loot = _activity[a + ActLoot],
                        Craft = _activity[a + ActCraft],
                        Gather = _activity[a + ActGather],
                        PingMs = (ushort)Mathf.Clamp((float)NetProbe.Ping(connection), 0, ushort.MaxValue),
                        PacketLoss = (ushort)Mathf.Clamp(NetProbe.Stat(connection, "PacketLossLastSecond"), 0, ushort.MaxValue),
                        ClientFps = (short)Mathf.Clamp((float)clientFps, -1, short.MaxValue),
                        ClientMemMB = (ushort)Mathf.Clamp((float)clientMem, 0, ushort.MaxValue)
                    };
                    Array.Clear(_activity, a, ActCount);

                    _samples[_sampleHead] = sample;
                    _sampleHead = (_sampleHead + 1) % _samples.Length;
                    if (_sampleCount < _samples.Length) _sampleCount++;
                    written++;

                    WritePlayerCsv(sample, player.userID, player.displayName);
                }
            }

            #region CSV

            private static void EnsureWriters()
            {
                string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
                if (day == _fileDay && _rowWriter != null) return;

                CloseWriters();
                bool newDay = _fileDay != null && day != _fileDay;
                _fileDay = day;
                _rowWriter = OpenCsv($"timeline-{day}.csv",
                    string.Join(",", RowColumns) + "," + string.Join(",", Cat.Names.Select(n => n + "_ms")));
                _playerWriter = OpenCsv($"players-{day}.csv",
                    "time,steamid,name,x,z,speed_mps,sleeping,mounted,wounded,dead,build,attack,loot,craft,gather,ping_ms,packet_loss,client_fps,client_mem_mb");
                if (newDay)
                    CompressOldFiles();
            }

            private static StreamWriter OpenCsv(string name, string header)
            {
                string path = Path.Combine(_dir, name);
                bool exists = File.Exists(path);

                // A file started by an older version has other columns: set it aside rather than append misaligned rows
                if (exists)
                {
                    string first = null;
                    try { using (var reader = new StreamReader(path)) first = reader.ReadLine(); } catch { }
                    if (first != null && first != header)
                    {
                        File.Move(path, Path.Combine(_dir, Path.GetFileNameWithoutExtension(name) + "-old-" + DateTime.UtcNow.ToString("HHmmss") + ".csv"));
                        exists = false;
                    }
                }

                var writer = new StreamWriter(path, true, new UTF8Encoding(false), 64 * 1024);
                if (!exists) writer.WriteLine(header);
                return writer;
            }

            private static void WriteRowCsv(Row r, int slot)
            {
                if (_dir == null) return;
                try
                {
                    EnsureWriters();
                    var ci = CultureInfo.InvariantCulture;
                    var w = _rowWriter;
                    w.Write(r.Time.ToString(ci)); w.Write(',');
                    w.Write(r.Frames.ToString(ci)); w.Write(',');
                    w.Write(r.WorstFrameMs.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.Update.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.LateUpdate.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.PreLateUpdate.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.FixedUpdate.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.Physics.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.TotalCpu.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.GcPauseMs.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.HeapMB.ToString(ci)); w.Write(',');
                    w.Write(r.Gc.ToString(ci)); w.Write(',');
                    w.Write(r.Invokes.ToString(ci)); w.Write(',');
                    w.Write(r.LoadBalancer.ToString(ci)); w.Write(',');
                    w.Write(r.Entities.ToString(ci)); w.Write(',');
                    w.Write(r.Players.ToString(ci)); w.Write(',');
                    w.Write(r.Spikes.ToString(ci)); w.Write(',');
                    w.Write(r.Spawned.ToString(ci)); w.Write(',');
                    w.Write(r.Killed.ToString(ci)); w.Write(',');
                    w.Write(r.NetInKB.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.NetOutKB.ToString("0.0", ci)); w.Write(',');
                    w.Write(r.PacketLoss.ToString(ci)); w.Write(',');
                    w.Write(r.PacketsOut.ToString(ci)); w.Write(',');
                    w.Write(r.Joining.ToString(ci)); w.Write(',');
                    w.Write(r.Queued.ToString(ci)); w.Write(',');
                    w.Write(r.SkinQueue.ToString(ci));
                    for (int c = 0; c < Cat.Count; c++)
                    {
                        w.Write(',');
                        w.Write(_cats[slot * Cat.Count + c].ToString("0.00", ci));
                    }
                    w.WriteLine();
                    if (++_unflushed >= 10)
                    {
                        _rowWriter.Flush();
                        _playerWriter.Flush();
                        _unflushed = 0;
                    }
                }
                catch (Exception e)
                {
                    _dir = null;
                    CloseWriters();
                    _instance?.PrintWarning("Timeline CSV disabled after write error: " + e.Message);
                }
            }

            private static void WritePlayerCsv(PlayerSample s, ulong steamId, string name)
            {
                if (_dir == null) return;
                try
                {
                    EnsureWriters();
                    var ci = CultureInfo.InvariantCulture;
                    _playerWriter.WriteLine(string.Join(",",
                        s.Time.ToString(ci), steamId.ToString(ci), CsvEscape(name), s.X.ToString(ci), s.Z.ToString(ci),
                        (s.SpeedDm / 10f).ToString("0.0", ci),
                        (s.Flags & 1) != 0 ? "1" : "0", (s.Flags & 2) != 0 ? "1" : "0", (s.Flags & 4) != 0 ? "1" : "0", (s.Flags & 8) != 0 ? "1" : "0",
                        s.Build.ToString(ci), s.Attack.ToString(ci), s.Loot.ToString(ci), s.Craft.ToString(ci), s.Gather.ToString(ci),
                        s.PingMs.ToString(ci), s.PacketLoss.ToString(ci), s.ClientFps.ToString(ci), s.ClientMemMB.ToString(ci)));
                }
                catch (Exception e)
                {
                    _dir = null;
                    CloseWriters();
                    _instance?.PrintWarning("Timeline CSV disabled after write error: " + e.Message);
                }
            }

            private static string CsvEscape(string value)
            {
                if (string.IsNullOrEmpty(value)) return string.Empty;
                if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            private static void CloseWriters()
            {
                try { _rowWriter?.Dispose(); } catch { }
                try { _playerWriter?.Dispose(); } catch { }
                _rowWriter = _playerWriter = null;
            }

            // Gzips previous days' CSVs and deletes files past the retention, on a worker thread
            private static void CompressOldFiles()
            {
                string dir = _dir;
                string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                int keepDays = _config.CsvRetentionDays;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        foreach (var path in Directory.GetFiles(dir, "*.csv"))
                        {
                            if (Path.GetFileName(path).Contains(today)) continue;
                            using (var input = File.OpenRead(path))
                            using (var output = File.Create(path + ".gz"))
                            using (var gzip = new GZipStream(output, CompressionMode.Compress))
                                input.CopyTo(gzip);
                            File.Delete(path);
                        }
                        foreach (var path in Directory.GetFiles(dir, "*.gz"))
                            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-keepDays))
                                File.Delete(path);
                    }
                    catch
                    {
                        // Housekeeping only; the next day change tries again
                    }
                });
            }

            #endregion

            #region Queries

            public static string RowsJson(long fromUnix, int max)
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(64 * 1024);
                sb.Append("{\"columns\":[");
                sb.Append(string.Join(",", RowColumns.Concat(Cat.Names.Select(n => n + "_ms")).Select(c => "\"" + c + "\"")));
                sb.Append("],\"world_size\":").Append(World.Size.ToString(ci));
                sb.Append(",\"rows\":[");

                int written = 0;
                int last = 0;
                int start = (_rowHead - _rowCount + _rows.Length) % Math.Max(1, _rows.Length);
                for (int i = 0; i < _rowCount && written < max; i++)
                {
                    int slot = (start + i) % _rows.Length;
                    var r = _rows[slot];
                    if (r.Time <= fromUnix) continue;
                    if (written > 0) sb.Append(',');
                    sb.Append('[').Append(r.Time.ToString(ci)).Append(',').Append(r.Frames.ToString(ci)).Append(',')
                      .Append(r.WorstFrameMs.ToString("0.0", ci)).Append(',').Append(r.Update.ToString("0.0", ci)).Append(',')
                      .Append(r.LateUpdate.ToString("0.0", ci)).Append(',').Append(r.PreLateUpdate.ToString("0.0", ci)).Append(',')
                      .Append(r.FixedUpdate.ToString("0.0", ci)).Append(',').Append(r.Physics.ToString("0.0", ci)).Append(',')
                      .Append(r.TotalCpu.ToString("0.0", ci)).Append(',').Append(r.GcPauseMs.ToString("0.0", ci)).Append(',')
                      .Append(r.HeapMB.ToString(ci)).Append(',').Append(r.Gc.ToString(ci)).Append(',')
                      .Append(r.Invokes.ToString(ci)).Append(',').Append(r.LoadBalancer.ToString(ci)).Append(',')
                      .Append(r.Entities.ToString(ci)).Append(',').Append(r.Players.ToString(ci)).Append(',')
                      .Append(r.Spikes.ToString(ci)).Append(',').Append(r.Spawned.ToString(ci)).Append(',').Append(r.Killed.ToString(ci)).Append(',')
                      .Append(r.NetInKB.ToString("0.0", ci)).Append(',').Append(r.NetOutKB.ToString("0.0", ci)).Append(',')
                      .Append(r.PacketLoss.ToString(ci)).Append(',').Append(r.PacketsOut.ToString(ci)).Append(',')
                      .Append(r.Joining.ToString(ci)).Append(',').Append(r.Queued.ToString(ci)).Append(',').Append(r.SkinQueue.ToString(ci));
                    for (int c = 0; c < Cat.Count; c++)
                        sb.Append(',').Append(_cats[slot * Cat.Count + c].ToString("0.00", ci));
                    sb.Append(']');
                    written++;
                    last = r.Time;
                }

                sb.Append("],\"last\":").Append(last.ToString(ci)).Append('}');
                return sb.ToString();
            }

            public static string PlayersJson(long fromUnix, int max)
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(64 * 1024);
                sb.Append("{\"columns\":[\"time\",\"player\",\"x\",\"z\",\"speed_dm\",\"flags\",\"build\",\"attack\",\"loot\",\"craft\",\"gather\",\"ping_ms\",\"packet_loss\",\"client_fps\",\"client_mem_mb\"]");
                sb.Append(",\"flags\":\"1 sleeping, 2 mounted, 4 wounded, 8 dead\",\"rows\":[");

                var used = new HashSet<int>();
                int written = 0;
                int last = 0;
                int start = (_sampleHead - _sampleCount + _samples.Length) % Math.Max(1, _samples.Length);
                for (int i = 0; i < _sampleCount && written < max; i++)
                {
                    var s = _samples[(start + i) % _samples.Length];
                    if (s.Time <= fromUnix) continue;
                    if (written > 0) sb.Append(',');
                    sb.Append('[').Append(s.Time.ToString(ci)).Append(',').Append(s.Player.ToString(ci)).Append(',')
                      .Append(s.X.ToString(ci)).Append(',').Append(s.Z.ToString(ci)).Append(',').Append(s.SpeedDm.ToString(ci)).Append(',')
                      .Append(s.Flags.ToString(ci)).Append(',').Append(s.Build.ToString(ci)).Append(',').Append(s.Attack.ToString(ci)).Append(',')
                      .Append(s.Loot.ToString(ci)).Append(',').Append(s.Craft.ToString(ci)).Append(',').Append(s.Gather.ToString(ci)).Append(',')
                      .Append(s.PingMs.ToString(ci)).Append(',').Append(s.PacketLoss.ToString(ci)).Append(',')
                      .Append(s.ClientFps.ToString(ci)).Append(',').Append(s.ClientMemMB.ToString(ci)).Append(']');
                    used.Add(s.Player);
                    written++;
                    last = s.Time;
                }

                sb.Append("],\"players\":{");
                bool first = true;
                foreach (int index in used.OrderBy(i => i))
                {
                    if (index >= PlayerIds.Count) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(index.ToString(ci)).Append("\":[\"").Append(PlayerIds[index].ToString(ci)).Append("\",")
                      .Append(JsonConvert.ToString(PlayerNames[index] ?? string.Empty)).Append(']');
                }
                sb.Append("},\"last\":").Append(last.ToString(ci)).Append('}');
                return sb.ToString();
            }

            public static string Status()
            {
                if (!_enabled) return "Timeline: off";
                double hours = _rowCount / 3600.0;
                long bytes = _rows.Length * 100L + _cats.Length * 4L + _samples.Length * 32L;
                return $"Timeline: {_rowCount:n0} second(s) stored ({hours:0.0}h of {_rows.Length / 3600.0:0.0}h), " +
                       $"{_sampleCount:n0} player sample(s), {PlayerIds.Count} player(s) seen, ~{bytes / 1048576.0:0.0}MB reserved; " +
                       $"CSV {(_dir != null ? "writing to " + _dir : "off")}";
            }

            #endregion
        }

        #endregion
    }
}
