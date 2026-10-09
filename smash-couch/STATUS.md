# Current status — October 9, 2026

The `.53` native Metal candidate has completed a diagnostic replay and ordinary close. It is not installed or promoted. The installed `.36` emulator/R3 launcher and current progress remain preserved.

## Source changes

The cumulative overlay includes the native Metal renderer, guarded depth/stencil handling, pipeline recipe caching, frame/presentation diagnostics, controller transport and input tracing, memory witnesses, allocation improvements, and the latest VI shutdown cancellation repair. The pipeline cache key ignores dormant blend fields only when their color attachment is invalid, matching the actual Metal descriptor construction. The shutdown repair handles canceled service requests without returning success with an invalid buffer slot.

The complete emulator source is pinned to Ryubing `eb6b0e9adca0c2f010fa6ac093c806c4db199283` plus this overlay. Its 5,175 source files match tree SHA-256 `9c0a0df73d3f506ead4760132d8c0be82bb0b255bc1b5fd4fc8d5c604bb695d0`.

## Native replay evidence

Run199 completed Kirby versus Fox, both CPU level 9, on Battlefield. The gameplay and result images were inspected, normal clock progression was supported, and all seven owned processes and endpoints closed. Game, runner, observer, controller carrier and end helper exited 0. The preceding Busy/NoSlots shutdown error pair was absent under the unchanged classifier.

| Presentation sample | Frames | Maximum interval | Above 33.34 ms / 100 ms |
| --- | ---: | ---: | ---: |
| Original 90 seconds | 5,399 | 16.6668 ms | 0 / 0 |
| Broader 175 seconds | 10,499 | 29.1665 ms | 0 / 0 |

The broader sample still contains 29 intervals at 25 ms, eight at 20.833 ms and one at 29.166 ms. This does not establish perfect pacing or physical input latency. Run199 used API validation on, global GPU shader validation off, eight server heaps, a 2.5 GiB managed limit and 1,823 retained recipe records. Its results are not a controlled speedup comparison with earlier scenes or cache conditions. Late Battlefield purple/black levels remain unresolved.

## Normal launcher and latest attempts

Run200 used the API-off normal profile without a managed heap cap. It reached native fullscreen startup, then refused a stale title review. No match occurred. The existing 8 GiB sampled process guard later stopped it. Managed-heap and whole-process limits measure different things; the memory split and cause remain unresolved.

The separately reviewed `53.2.0` launcher restores `DOTNET_gcServer=1`, `DOTNET_GCHeapCount=8`, and `DOTNET_GCHeapHardLimit=0xA0000000`, with API and global shader validation off. The 8 GiB sampled process guard remains unchanged. This capped normal profile has source/CPU/build evidence and has not passed a full normal-profile replay.

Run201 closed before a match after a transitional title image. Prepared AC Run202 was retained. The user's authorized battery revision, Run203, started and closed normally, but its fresh image showed How to Play rather than the expected prompt. It did not reach a match or yield a performance sample. At this publication checkpoint, the live owner is preparing Run204, a short existing input-trace diagnostic to distinguish sender acknowledgment from Player 1 HID publication. That work is outside this frozen source snapshot. Battery observations must not be described as power-matched to earlier AC results.

## UI preview

The current UI uses a light layout, simpler Home screen and controller-first preset selection. Jump, Quick attack and Special appear as separate rows with button badges. Xbox, PlayStation, Switch, Auto and Other labels are supported. Xbox A-to-jump and Brawlhalla-style face-button presets are included. Brawlhalla-style describes the mapping; it does not implement Brawlhalla mechanics.

The preview has 105 regression, 29 presentation/isolation and 35 controller-preset checks recorded. Its Play action records intent only. The original live model is retained as source and excluded from the preview build. Live integration, physical mappings/reconnection, multiple controllers, TV presentation, current-data launch/close checks and promotion remain open.
