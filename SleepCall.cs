using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SleepCall
{
	/*
		One player in bed is enough to skip the night - but nobody gets a black screen without
		warning. When someone lies down the server announces it to everyone with a countdown,
		and when the countdown ends it waits, with reasons, for anyone who is fighting or
		travelling. Players can still all go to bed to skip immediately, as in vanilla.

		Server-side only. Vanilla's own sleep loop does the actual skip: Game.UpdateSleeping
		asks Game.EverybodyIsTryingToSleep every frame during afternoon and night, and on a
		yes it calls EnvMan.SkipToMorning and broadcasts SleepStart. This mod replaces that
		one question with its own answer; everything after it is unchanged vanilla.
	*/
	[BepInPlugin(Guid, Name, Version)]
	public class SleepCallPlugin : BaseUnityPlugin
	{
		public const string Guid = "liekos47.sleepcall";
		public const string Name = "SleepCall";
		public const string Version = "1.0.2";

		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> Enabled;
		internal static ConfigEntry<int> CountdownSeconds;
		internal static ConfigEntry<string> ReminderSeconds;
		internal static ConfigEntry<int> BannerIntervalSeconds;
		internal static ConfigEntry<bool> TopLeftFeed;
		internal static ConfigEntry<bool> ChatAnnounce;
		internal static ConfigEntry<bool> WaitForCombat;
		internal static ConfigEntry<float> RecentDamageSeconds;
		internal static ConfigEntry<float> AlertedMonsterRange;
		internal static ConfigEntry<bool> WaitForTravel;
		internal static ConfigEntry<float> TravelSpeed;
		internal static ConfigEntry<int> MaxWaitSeconds;
		internal static ConfigEntry<int> WaitReminderSeconds;
		internal static ConfigEntry<bool> DebugLog;

		private Harmony harmony;

		private void Awake()
		{
			Log = Logger;

			Enabled = Config.Bind("General", "Enabled", true,
				"Enable the mod. When disabled the vanilla rule applies: everyone must be in bed.");
			CountdownSeconds = Config.Bind("Countdown", "CountdownSeconds", 60,
				"Seconds between the first player lying down and the night skipping, so everyone is warned.");
			ReminderSeconds = Config.Bind("Countdown", "ReminderSeconds", "30,10,5",
				"Comma-separated seconds-remaining at which a reminder is also posted to the top-left feed.");
			BannerIntervalSeconds = Config.Bind("Countdown", "BannerIntervalSeconds", 10,
				"Re-show the centre-screen countdown banner this often, with the time left. Centre messages fade after about 4 seconds, so a single one is easy to miss. 0 = only at the start and the reminders.");
			TopLeftFeed = Config.Bind("Countdown", "TopLeftFeed", true,
				"Also post the countdown start, reminders, waiting reasons and the outcome to the top-left message feed, which stays on screen longer, queues instead of replacing, and is kept in the Compendium's message log.");
			ChatAnnounce = Config.Bind("Countdown", "ChatAnnounce", false,
				"EXPERIMENTAL, untested: also post the countdown start and outcome as a chat shout from 'SleepCall'. Clients run a platform permission check on the sender of every chat line, and a server-made sender has no platform identity, so it may be silently dropped. Safe to try live: the config reloads every 30 s.");
			WaitForCombat = Config.Bind("Waiting", "WaitForCombat", true,
				"After the countdown, keep waiting while any awake player is fighting.");
			RecentDamageSeconds = Config.Bind("Waiting", "RecentDamageSeconds", 15f,
				"A player counts as fighting if their health dropped within this many seconds.");
			AlertedMonsterRange = Config.Bind("Waiting", "AlertedMonsterRange", 40f,
				"A player counts as fighting if an alerted, untamed monster is within this many metres.");
			WaitForTravel = Config.Bind("Waiting", "WaitForTravel", true,
				"After the countdown, keep waiting while any awake player is travelling.");
			TravelSpeed = Config.Bind("Waiting", "TravelSpeed", 3f,
				"A player counts as travelling above this average speed in m/s (walking is about 2, running 7, sailing 4-8).");
			MaxWaitSeconds = Config.Bind("Waiting", "MaxWaitSeconds", 300,
				"Give up waiting after this many seconds and skip the night anyway, with a final warning. 0 = wait forever.");
			WaitReminderSeconds = Config.Bind("Waiting", "WaitReminderSeconds", 15,
				"While waiting, repeat who is holding things up every this many seconds.");
			DebugLog = Config.Bind("General", "DebugLog", false, "Log every decision to the server log.");

			harmony = new Harmony(Guid);
			harmony.PatchAll();
			Log.LogInfo($"{Name} {Version} loaded");
			// Re-read the config file every 30 s so settings (including DebugLog) can be changed
			// without a server restart. BepInEx does not watch the file on its own.
			InvokeRepeating(nameof(ReloadConfig), 30f, 30f);
		}

		private void ReloadConfig()
		{
			try { Config.Reload(); } catch (Exception e) { Log.LogWarning($"config reload failed: {e.Message}"); }
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	[HarmonyPatch(typeof(Game), "EverybodyIsTryingToSleep")]
	internal static class Game_EverybodyIsTryingToSleep_Patch
	{
		private static bool Prefix(ref bool __result)
		{
			if (!SleepCallPlugin.Enabled.Value)
			{
				return true;
			}
			__result = SleepCoordinator.Tick();
			return false;
		}
	}

	internal static class SleepCoordinator
	{
		private enum State { Idle, Counting, Waiting, Skipping }

		private struct Sample
		{
			public Vector3 pos;
			public float health;
			public float time;
			public float lastDamageTime;
			public float speed;
		}

		private static State state = State.Idle;
		private static float startedAt;
		private static float lastBanner;
		private static float lastWaitReminder;
		private static bool chatFailed;
		private static float lastSample;
		private static readonly HashSet<int> firedReminders = new HashSet<int>();
		private static readonly Dictionary<ZDOID, Sample> samples = new Dictionary<ZDOID, Sample>();
		private static readonly Dictionary<int, bool> monsterPrefabs = new Dictionary<int, bool>();
		private static readonly List<ZDO> scratch = new List<ZDO>();

		private const float SampleInterval = 1f;
		// Same value vanilla passes to ZRoutedRpc for a broadcast (ZRoutedRpc.Everybody).
		private const long Everybody = 0L;

		public static bool Tick()
		{
			float now = Time.unscaledTime;
			List<ZDO> characters = ZNet.instance.GetAllCharacterZDOS();
			if (characters.Count == 0)
			{
				Reset();
				return false;
			}

			if (now - lastSample >= SampleInterval)
			{
				UpdateSamples(characters, now);
				lastSample = now;
			}

			var sleepers = new List<ZDO>();
			var awake = new List<ZDO>();
			foreach (ZDO zdo in characters)
			{
				if (zdo.GetBool(ZDOVars.s_inBed, false)) sleepers.Add(zdo); else awake.Add(zdo);
			}

			if (sleepers.Count == 0)
			{
				if (state == State.Counting || state == State.Waiting)
				{
					Info("cancelled: nobody in bed any more");
					Broadcast("Nobody is in bed any more - the night goes on.", feed: true, chat: true);
				}
				Reset();
				return false;
			}

			// Everyone in bed: vanilla behaviour, skip at once.
			if (awake.Count == 0)
			{
				if (state != State.Skipping) Info($"everyone in bed ({characters.Count} of {characters.Count}), skipping at once");
				state = State.Skipping;
				return true;
			}

			if (state == State.Idle || state == State.Skipping)
			{
				state = State.Counting;
				startedAt = now;
				lastBanner = now;
				firedReminders.Clear();
				lastWaitReminder = -1000f;
				string who = Names(sleepers);
				Info($"countdown started: {who} in bed, {awake.Count} awake ({Names(awake)}), {SleepCallPlugin.CountdownSeconds.Value}s");
				Broadcast($"{who} went to bed. The night will pass in {SleepCallPlugin.CountdownSeconds.Value} seconds.", feed: true, chat: true);
				Feed("Fighting or travelling delays it. Go to bed too to skip sooner.");
				return false;
			}

			float elapsed = now - startedAt;
			int remaining = SleepCallPlugin.CountdownSeconds.Value - (int)elapsed;
			if (remaining > 0)
			{
				bool reminded = false;
				foreach (int mark in ReminderMarks())
				{
					if (remaining <= mark && firedReminders.Add(mark))
					{
						Broadcast($"The night passes in {mark} seconds.", feed: true, chat: false);
						lastBanner = now;
						reminded = true;
						break;
					}
				}
				// Centre text fades after about 4 s; keep it coming so nobody misses the whole countdown.
				int interval = SleepCallPlugin.BannerIntervalSeconds.Value;
				if (!reminded && interval > 0 && now - lastBanner >= interval)
				{
					lastBanner = now;
					string who = Names(sleepers);
					Center($"{who} {(sleepers.Count == 1 ? "is" : "are")} in bed - the night passes in {remaining} seconds.");
				}
				return false;
			}

			List<string> blockers = Blockers(awake, now);
			int waited = (int)elapsed - SleepCallPlugin.CountdownSeconds.Value;
			bool gaveUp = SleepCallPlugin.MaxWaitSeconds.Value > 0 && waited >= SleepCallPlugin.MaxWaitSeconds.Value;

			if (blockers.Count == 0 || gaveUp)
			{
				Broadcast(gaveUp
					? "Waited long enough - the night passes now."
					: "Everyone is clear. The night passes now.", feed: true, chat: true);
				Info(gaveUp
					? $"skipping: gave up after waiting {waited}s, still blocked by: {string.Join("; ", blockers)}"
					: $"skipping: countdown done after {(int)elapsed}s, nobody fighting or travelling");
				state = State.Skipping;
				return true;
			}

			state = State.Waiting;
			if (now - lastWaitReminder >= SleepCallPlugin.WaitReminderSeconds.Value)
			{
				bool first = lastWaitReminder < 0f;
				lastWaitReminder = now;
				Broadcast("Waiting to sleep: " + string.Join(", ", blockers), feed: true, chat: first);
				if (first) Info("waiting: " + string.Join("; ", blockers)); else Debug("waiting: " + string.Join("; ", blockers));
			}
			return false;
		}

		private static void Reset()
		{
			state = State.Idle;
			firedReminders.Clear();
		}

		private static IEnumerable<int> ReminderMarks()
		{
			foreach (string part in SleepCallPlugin.ReminderSeconds.Value.Split(','))
			{
				if (int.TryParse(part.Trim(), out int v) && v > 0) yield return v;
			}
		}

		// Position and health are read once a second; from those we get speed and "took damage recently".
		private static void UpdateSamples(List<ZDO> characters, float now)
		{
			var seen = new HashSet<ZDOID>();
			foreach (ZDO zdo in characters)
			{
				seen.Add(zdo.m_uid);
				Vector3 pos = zdo.GetPosition();
				float health = zdo.GetFloat(ZDOVars.s_health, 0f);
				if (samples.TryGetValue(zdo.m_uid, out Sample prev))
				{
					float dt = Mathf.Max(now - prev.time, 0.001f);
					float instant = Vector3.Distance(pos, prev.pos) / dt;
					// Smoothed so one teleport or portal hop does not read as sustained travel.
					prev.speed = Mathf.Lerp(prev.speed, Mathf.Min(instant, 30f), 0.5f);
					if (health < prev.health - 0.5f) prev.lastDamageTime = now;
					prev.pos = pos;
					prev.health = health;
					prev.time = now;
					samples[zdo.m_uid] = prev;
				}
				else
				{
					samples[zdo.m_uid] = new Sample { pos = pos, health = health, time = now, lastDamageTime = -1000f, speed = 0f };
				}
			}
			foreach (ZDOID gone in samples.Keys.Where(k => !seen.Contains(k)).ToList())
			{
				samples.Remove(gone);
			}
		}

		private static List<string> Blockers(List<ZDO> awake, float now)
		{
			var result = new List<string>();
			foreach (ZDO zdo in awake)
			{
				if (zdo.GetBool(ZDOVars.s_dead, false)) continue;
				string name = NameOf(zdo);
				samples.TryGetValue(zdo.m_uid, out Sample s);

				if (SleepCallPlugin.WaitForCombat.Value)
				{
					if (now - s.lastDamageTime <= SleepCallPlugin.RecentDamageSeconds.Value)
					{
						result.Add($"{name} is taking damage");
						continue;
					}
					if (AlertedMonsterNear(zdo.GetPosition()))
					{
						result.Add($"{name} is in combat");
						continue;
					}
				}
				if (SleepCallPlugin.WaitForTravel.Value && s.speed > SleepCallPlugin.TravelSpeed.Value)
				{
					result.Add($"{name} is travelling");
				}
			}
			return result;
		}

		// Any alerted, untamed monster within range of the point, found through the ZDOs in the
		// surrounding sectors - the server has no GameObjects for these, only ZDOs.
		private static bool AlertedMonsterNear(Vector3 point)
		{
			float range = SleepCallPlugin.AlertedMonsterRange.Value;
			scratch.Clear();
			ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(point), new SimulationDistance(1, 0, true), scratch, null);
			float rangeSq = range * range;
			foreach (ZDO zdo in scratch)
			{
				if (!zdo.GetBool(ZDOVars.s_alert, false)) continue;
				if (zdo.GetBool(ZDOVars.s_tamed, false)) continue;
				if ((zdo.GetPosition() - point).sqrMagnitude > rangeSq) continue;
				if (IsMonster(zdo.GetPrefab())) return true;
			}
			return false;
		}

		private static bool IsMonster(int prefabHash)
		{
			if (!monsterPrefabs.TryGetValue(prefabHash, out bool isMonster))
			{
				GameObject prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(prefabHash) : null;
				isMonster = prefab && prefab.GetComponent<MonsterAI>();
				monsterPrefabs[prefabHash] = isMonster;
			}
			return isMonster;
		}

		private static string NameOf(ZDO zdo)
		{
			string name = zdo.GetString(ZDOVars.s_playerName, "");
			return name.Length > 0 ? name : "Someone";
		}

		private static string Names(List<ZDO> zdos)
		{
			var names = zdos.Select(NameOf).Distinct().ToList();
			if (names.Count == 1) return names[0];
			return string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
		}

		// Centre banner always; top-left feed and chat when asked for and enabled in config.
		private static void Broadcast(string text, bool feed, bool chat)
		{
			Center(text);
			if (feed) Feed(text);
			if (chat) Chat(text);
		}

		private static void Center(string text) => Announce(MessageHud.MessageType.Center, text);

		private static void Feed(string text)
		{
			if (SleepCallPlugin.TopLeftFeed.Value) Announce(MessageHud.MessageType.TopLeft, text);
		}

		// MessageHud registers "ShowMessage" (type, text) on every client; the server can route it to all.
		// This is the same route vanilla's MessageHud.MessageAll uses.
		private static void Announce(MessageHud.MessageType type, string text)
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(Everybody, "ShowMessage", (int)type, text);
			Debug($"announce [{type}] {text}");
		}

		// Chat registers "ChatMessage" (position, type, UserInfo, text) on every client. Experimental:
		// the receiving client checks the sender's platform permissions before showing it, and a
		// server-made UserInfo has no platform identity. Off by default.
		private static void Chat(string text)
		{
			if (!SleepCallPlugin.ChatAnnounce.Value || chatFailed) return;
			try
			{
				var sender = new UserInfo { Name = "SleepCall" };
				ZRoutedRpc.instance.InvokeRoutedRPC(Everybody, "ChatMessage", Vector3.zero, (int)Talker.Type.Shout, sender, text);
				Debug($"chat {text}");
			}
			catch (Exception e)
			{
				chatFailed = true;
				SleepCallPlugin.Log.LogWarning($"chat announce failed, disabled until restart: {e.GetType().Name}: {e.Message}");
			}
		}

		// A few lines per night, always: enough to reconstruct what happened from the server log.
		private static void Info(string text)
		{
			SleepCallPlugin.Log.LogInfo(text);
		}

		private static void Debug(string text)
		{
			if (SleepCallPlugin.DebugLog.Value) SleepCallPlugin.Log.LogInfo(text);
		}
	}
}
