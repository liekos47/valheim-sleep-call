# SleepCall

One player in bed is enough to skip the night - but nobody gets a black screen without
warning.

In vanilla Valheim every player must be in bed before the night can pass. Mods that relax
that to "one player" have a side effect: the moment someone lies down, everyone else fades
to black - mid-fight, mid-voyage, mid-anything. SleepCall keeps the one-player rule and
fixes the surprise.

## What happens

1. A player lies down. Everyone sees **"Astrid went to bed. The night will pass in 60
   seconds."** in the centre of the screen and in the top-left feed, with a note that
   fighting or travelling delays it.
2. The centre banner repeats every 10 seconds with the time left ("Astrid is in bed - the
   night passes in 40 seconds"), and reminders at 30, 10 and 5 seconds also go to the feed.
3. When the countdown ends, the server checks every player who is still up:
   - **fighting** - took damage in the last 15 s, or an alerted monster is within 40 m
   - **travelling** - moving faster than 3 m/s on average (running, sailing, riding)
4. If anyone is, everyone sees **"Waiting to sleep: Bjorn is in combat, Sigrid is travelling"**,
   repeated every 15 s until they are clear. After 5 minutes it gives up and skips anyway,
   with a final warning.
5. The night passes exactly as in vanilla (same fade, same morning, same rested buff).

If everyone goes to bed, the night skips immediately, as it always has. If the sleeper gets
up during the countdown, it is cancelled and everyone is told.

Missed a message? The top-left ones are kept in the Compendium's message log.

## Install

Server only. Copy `SleepCall.dll` into `BepInEx/plugins` on the dedicated server and
restart it. Clients need nothing. Remove any other "one player sleeps" mod first - two mods
answering the same question will fight.

## Config

`BepInEx/config/liekos47.sleepcall.cfg`, created on first start. The file is re-read every
30 seconds, so changes apply without a restart.

| Setting | Default | Meaning |
|---|---|---|
| `CountdownSeconds` | 60 | warning time before the night passes |
| `ReminderSeconds` | 30,10,5 | when reminders are also posted to the feed |
| `BannerIntervalSeconds` | 10 | how often the centre banner repeats; 0 = start and reminders only |
| `TopLeftFeed` | true | also post start, reminders, waiting and outcome to the top-left feed |
| `ChatAnnounce` | false | experimental: also shout the start and outcome in chat |
| `WaitForCombat` | true | hold while anyone is fighting |
| `RecentDamageSeconds` | 15 | "fighting" if health dropped this recently |
| `AlertedMonsterRange` | 40 | "fighting" if an alerted monster is this close |
| `WaitForTravel` | true | hold while anyone is travelling |
| `TravelSpeed` | 3 | "travelling" above this m/s (walk ~2, run ~7, sail 4-8) |
| `MaxWaitSeconds` | 300 | give up waiting after this long; 0 = never |
| `WaitReminderSeconds` | 15 | how often the waiting message repeats |
| `DebugLog` | false | log every message sent, not just the decisions |

`ChatAnnounce` is off because a client checks the platform identity of whoever sent a chat
line before showing it, and a line sent by the server has no such identity. It may simply
not appear. It is harmless to try.

## Server log

SleepCall writes a few lines per night to `BepInEx/LogOutput.log`, whatever `DebugLog` is
set to:

```
[Info   : SleepCall] countdown started: Astrid in bed, 3 awake (Bjorn, Sigrid and Leif), 60s
[Info   : SleepCall] waiting: Bjorn is in combat
[Info   : SleepCall] skipping: countdown done after 61s, nobody fighting or travelling
[Info   : SleepCall] cancelled: nobody in bed any more
[Info   : SleepCall] everyone in bed (2 of 2), skipping at once
```

Note the space in `[Info   : SleepCall]`: BepInEx right-aligns source names to 10
characters. Search with `: *SleepCall]`, not `:SleepCall]`.

## How it works

Vanilla's own sleep loop (`Game.UpdateSleeping`) asks one question every frame during the
afternoon and night: `EverybodyIsTryingToSleep()`. On yes it calls `EnvMan.SkipToMorning`
and tells every client to fade. SleepCall replaces the answer to that question with its
countdown and checks, and leaves everything after it to vanilla. It reads the same
synchronised player data the server already has (bed state, position, health) and looks
at nearby monster data for the alert flag, so it works on a dedicated server with no
client-side component. Messages use the same `ShowMessage` route as vanilla's own
broadcast messages.

## Known limits

- "Taking damage" is any health drop, so environmental damage (freezing, poison, burning)
  also counts as fighting.
- "In combat" means an alerted monster is within range, even if it is not attacking.
- Travel speed is sampled once a second and smoothed, so a portal jump reads as a short
  burst, not sustained travel.

## Building

```
dotnet build SleepCall.csproj -c Release
```

Compiles against the game and BepInEx assemblies in `refs\` (see `Environment.props`).
Output: `bin\Release\SleepCall.dll`, plus a Thunderstore-style package in `pkg\`.
`hooks.txt` lists every game member the mod uses, for checking against a new game version
with HookCheck.

Built against Valheim `l-1.0.16` with BepInExPack 5.4.2333.
