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

The official background is currently disabled on this machine with `scripts/vendor-service.ps1` (`-Action status|disable|restore`). Mode switching, monitoring, Fn keys, sleep and wake, and reboot all passed; see `docs/protocol.md`.

Still open: charging. The user's monitor powers the laptop over USB-C, so unplugging also loses the display. When the battery has dropped to about 85-90% from normal use, recharge on the monitor cable, then run `scripts/battery-read.ps1 -Label after-recharge`. If it stops early, repeat with the original adapter.

If anything needed is lost, run `restore`. Do not suggest uninstalling the official software until this passes. An uninstall might also remove `inpoutx64`, which fan reading needs, so check that first.

## Next feature: battery charge limit

Thresholds read `enabled=0, lower=0, upper=0` with `battery-read.ps1`. Write commands have never been sent here, and BIOS 1.01 is unverified for them. Tell the user the exact commands before a single test write. Testing also needs the battery below the limit, so it waits for the same discharge.
