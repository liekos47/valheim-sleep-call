## 1.0.3

- The last seconds are counted down one by one in the centre of the screen: "Sleeping in 5",
  4, 3, 2, 1, then "Sleeping now" (`FinalCountdownSeconds`, 5; 0 = off). Before, the last
  message was "The night passes in 5 seconds" and then the screen went black.
- The final count also runs after a wait for fighting or travelling ends, so the fade never
  follows straight on from "Waiting to sleep". If someone starts fighting or travelling during
  the count, it goes back to waiting.
- Reminders at or below `FinalCountdownSeconds` are skipped; the final count covers them.
- Fixed: a countdown left unfinished when the night ended on its own, or when the mod was
  disabled in the config, could carry on from where it stopped the next time someone lay down.
- The config file is only re-read when it has changed, and the log says when it was.
- Less work per frame during a countdown (no list or reminder parsing allocations).

## 1.0.2

- The centre-screen countdown banner repeats every 10 s with the time left and who is in bed
  (`BannerIntervalSeconds`). Centre messages fade after about 4 s, so a single banner at the
  start was easy to miss entirely.
- The countdown start, reminders, waiting reasons and outcome also go to the top-left feed
  (`TopLeftFeed`, on), which stays longer, queues rather than replacing, and is kept in the
  Compendium's message log.
- Optional chat shout for the start and outcome (`ChatAnnounce`, off, experimental and untested:
  clients may drop chat from a server-made sender).
- The first "waiting" message of a night is always logged, not only with DebugLog.
- Built against Valheim l-1.0.16.

## 1.0.1

- Always log countdown start, who is awake, the skip decision and cancellations (a few lines per night).
- Re-read the config file every 30 s, so settings including DebugLog apply without a restart.

## 1.0.0

- One player in bed skips the night after an announced countdown (default 60 s) with reminders.
- After the countdown, waits while any awake player is fighting (recent damage or an alerted
  monster nearby) or travelling (sustained speed), naming who and why, then skips. Gives up
  after a configurable limit.
- Everyone in bed still skips immediately; a sleeper getting up cancels with a message.
- Server-side only. Built against Valheim l-1.0.14.
