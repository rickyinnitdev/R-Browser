# Memory optimization

Launch dist-optimized/R1.exe after closing the existing browser. The current YouTube session was left running.

Changes:
- Background and restored tabs initialize WebView2 only when selected.
- Idle background tabs suspend after 45 seconds (checked every 10 seconds). Audio-playing and fullscreen tabs are protected; WebView2 may decline suspension for other protected workloads.
- Reactivating a tab preserves page state. An activation/suspension race resumes the tab instead of leaving it frozen.
- Minimized active views request WebView2 Low memory mode; restoring the window restores Normal mode. This is a best-effort cache/paging hint, not a memory cap.
- Favicon cache is limited to 64 images, with bounded downloads and shared image ownership. Closing a tab cannot dispose another tab's cached icon.
- Cosmetic CSS cache is limited to 24 entries and 1,000,000 UTF-16 characters (~2 MB payload). Generic-only CSS shares the same string.
- Filter parser deduplication data is released, and immutable arrays are resized to actual use.
- Large domain CSS is delivered to the top document via the existing host handshake instead of being embedded in every frame's startup script.
- Unchanged startup scripts are reused; registration updates are serialized.
- Closed windows release tab views, cached bitmaps, timers, and shortcut message-filter references.

Validation:
- 17 real WebView2/filter lifecycle checks passed in D:/R1-memory-tests, including lazy loading, suspend/resume with preserved state, cosmetic handshake, minimized playback, background audio, and close-tab disposal.
- Bundled filter retained managed memory: 23.35 MiB before, 17.68 MiB after (5.66 MiB saved), measured after GC with the same rules. This is a filter-only measurement, not a total browser RAM claim.
- tools/test-protection.mjs checks popup intent, settings, and stylesheet preservation.

Use tools/measure-memory.ps1 for read-only process-tree samples after restarting. It never closes or launches R1. Full working-set totals can double-count shared pages; private committed memory is not the same as Task Manager's private working-set column.

No total YouTube RAM reduction is claimed: active video still needs decoder buffers, page scripts, and renderers.
