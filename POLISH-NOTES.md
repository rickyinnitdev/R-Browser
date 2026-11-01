# Browser polish & Sidebar tabs

Run dist/R1.exe after closing the previous browser instance.

- Sidebar Tabs (Vertical Tabs) with Option to Choose:
  - Added custom GDI+ owner-drawn `TabSidebar` with expanded (210px) and collapsed icon-only (48px) modes.
  - Seamless layout toggle between Top Tabs (horizontal) and Sidebar Tabs (vertical) via toolbar button (`[||]`), right-click tab context menu, `r1://settings`, or shortcut `Ctrl+Shift+V`.
  - When in sidebar mode, top chrome condenses to a sleek 34px toolbar, freeing up 30px of vertical viewing space.
  - Live drag-and-drop tab reordering in both horizontal and vertical tab modes.
  - Side panel docking position option (Left vs Right) in settings.
- Bug fixes:
  - Fixed horizontal `_tabScroll` clamping in `RecalcLayout` to prevent tabs from sliding offscreen when closing tabs or resizing.
  - Fixed borderless window taskbar overlap on maximize by handling `WM_GETMINMAXINFO` to respect the screen's working area.
  - Fixed unhandled `Ctrl+B` shortcut for Bookmarks in `HandleShortcut`.
  - Added `Ctrl+Shift+R` and `Ctrl+F5` for hard reload without cache.
  - Fixed download removal in SidePanel to cancel active download streams and persist deletion.
  - Fixed SidePanel download row coordinate overlap where progress bars drew over subtitle text.
  - Added middle-click in SidePanel to open bookmarks and history in background tabs.
  - Fixed `FindBar` status persisting after clearing query text.
  - Fixed `SuggestPopup` coordinate calculation using `PointToScreen` for multi-monitor / DPI accuracy.
  - Added "Reopen closed tab" to the tab context menu.

Validation: Release build and self-contained win-x64 publish succeeded with 0 warnings/errors (109 MB self-contained). `tools/test-protection.mjs` passed 13/13 checks. `tools/r1-eval.ps1` verified memory usage at ~120 MB RAM, well under the 150 MB ceiling.
