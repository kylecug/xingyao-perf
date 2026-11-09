# xingyao-perf

Tiny tray app for one laptop: MECHREVO Xingyao 14, board `XINGYAO Series-P916F-PTL`, Core Ultra X7 358H, BIOS 1.01. It switches performance modes and shows monitoring values on the taskbar. Keep it small: one exe built by `build.ps1` with the `csc.exe` in Windows (C# 5 only). The user writes in Chinese.

Protocol details and test results are in `docs/protocol.md`.

## Safety

- Writing to firmware or the EC can harm the machine. Only send commands already verified in `docs/protocol.md`, and tell the user before anything new.
- Fan writes are documented but have never been tested here.
- Claude Code runs without admin rights. Anything needing admin must be run by the user in their own administrator window; sending keystrokes to that window is blocked by Windows.

## Status (2026-11-09)

Done and confirmed by the user on the taskbar: three modes, monitoring bars, embedded overlay that survives opening Start.

Reboot auto-start and kept mode are confirmed from logs; the user has not yet confirmed the values on the taskbar after reboot. Not yet confirmed by the user: fullscreen hide, Explorer restart recovery.

## Running without the official background

The official background is currently disabled on this machine with `scripts/vendor-service.ps1` (`-Action status|disable|restore`). Mode switching, monitoring, Fn keys, sleep and wake, reboot, and charging all passed; see `docs/protocol.md`. With the background off, the battery charges to 100% with no limit, so the only known loss is charge protection.

If anything needed is lost, run `restore`. Before suggesting an uninstall of the official software, check whether it would remove `inpoutx64`, which fan reading needs.

## Next feature: battery charge limit

Thresholds read `enabled=0, lower=0, upper=0` with `battery-read.ps1`. Write commands have never been sent here, and BIOS 1.01 is unverified for them. Tell the user the exact commands before a single test write. Testing needs the battery below the limit before plugging in, for example under 80% for an 80% limit.
