# R1 Browser — Core Principles & Architecture Rules

> **MANDATORY INSTRUCTION FOR ALL AI ASSISTANTS & CONTRIBUTORS**
> Whenever you are asked to modify, extend, or add features to R1, you **MUST** strictly adhere to the three foundational pillars below. If a proposed feature conflicts with these principles, you must reject, simplify, or optimize it to preserve the project's identity.

---

## 🏛️ The Three Foundational Pillars

### 1. Minimal Browser (Minimalist Design & Architecture)
* **Zero UI Bloat:** No ribbons, complex multi-layer menus, toolbars, side-docks, or widgets. The chrome is clean, sleek, and stays under 64px total height.
* **Hand-Drawn WinForms Chrome:** All UI elements (tabs, address pill, buttons, menus, traffic lights) are custom owner-drawn with GDI+ and anti-aliasing. Never add WPF, WinUI, Electron, or heavy UI frameworks.
* **No Telemetry, No Accounts, No Cloud Sync:** R1 never phones home. No analytics, no metrics, no crash reporting servers, no background sync.
* **Bundled Local Pages:** Internal pages (`r1://newtab`, `r1://settings`) are pure, zero-dependency HTML/CSS loaded via virtual host mapping (`https://r1.internal/`). No remote CDN dependencies, no external scripts.

### 2. Lowest Possible RAM Usage (Every Megabyte Counts)
R1 is designed to be the lightest Chromium-based browser on Windows.
* **Software Rendering by Default:** Hardware acceleration (GPU process) is off by default. This saves ~100 MB of private working set immediately upon startup.
* **Strict Renderer Limit:** Capped at `--renderer-process-limit=3`. Multiple tabs share processes to prevent Chromium process explosion.
* **Two-Tier Tab Lifecycle:**
  1. **Tier 1 (Suspend @ 45s):** Inactive tabs call `TrySuspendAsync()` to shed memory without reloading.
  2. **Tier 2 (Hibernate @ 15m):** Idle background tabs completely dispose their `WebView2` control, releasing rendering engines entirely. They reinitialize lazily only when clicked.
* **Memory Target Hints:** Inactive background tabs and minimized windows must receive `CoreWebView2MemoryUsageTargetLevel.Low`.
* **Zero-Allocation Hot Paths:** The ad blocker and URL matching engine check hundreds of requests per second. Use `ReadOnlySpan<char>`, `CollectionsMarshal` / alternate lookups, and unmanaged structs. **Never allocate strings in the match loops.**
* **Bounded Caches:**
  - Disk cache: 32 MB
  - Media cache: 16 MB
  - CSS cache: 24 entries / 1,000,000 chars
  - Favicon cache: 64 entries
  - Blocked URL log: 100 entries max
* **Working Set Trimming:** Call `Native.TrimWorkingSet()` (`EmptyWorkingSet`) after closing tabs or hibernating.

### 3. Best Ad Blocker & Wi-Fi / ISP Privacy Protection
R1 includes an integrated, zero-overhead ad and tracking blocker that matches uBlock Origin quality without extension engine overhead.
* **In-Process Network Engine:** 120,000+ ABP network rules compiled into a continuous character blob and token hash buckets. Requests are answered locally with `403 Forbidden` via pooled streams (`Stream.Null`).
* **Cosmetic Element Hiding:** 24,000+ cosmetic selectors injected via CSS sheets, hiding empty ad containers, sticky banners, and cookie consent walls.
* **Anti-Adblock Neutralization:** Spoofs detector globals (`canRunAds`, `FuckAdBlock`, `adsbygoogle`), neutralizes bait elements offscreen, and dynamically strips anti-adblock overlay modals and scroll freezes.
* **YouTube Ad Immunity:** Native 250ms polling loop auto-skips pre-rolls, mutes ad playback, and dismisses enforcement dialogs.
* **Wi-Fi / ISP Tracking Protection:**
  - **Encrypted DNS (DNS-over-HTTPS):** DNS queries are sent over TLS to Cloudflare / Quad9. The local Wi-Fi router, coffee shop admin, or ISP cannot see which domains you visit via UDP 53 snooping.
  - **Encrypted Client Hello (ECH):** Encrypts the TLS Server Name Indication (SNI) so eavesdroppers on the local network cannot read the target hostname from packet headers.
  - **DNS Prefetch Disabled:** Never speculatively query DNS for on-page links.
  - **Hyperlink Auditing (`<a ping>`) Blocked:** Prevents websites from sending background tracking pings on link clicks.
  - **WebRTC IP Leak Protection:** Enforces strict permission checks to prevent local/private IP disclosure over local networks.

---

## 🚫 Forbidden in R1

Any AI or contributor reviewing or writing code for R1 must **NEVER**:
1. ❌ Add Chromium extension manifest/host support (introduces massive RAM overhead).
2. ❌ Add Node.js, Electron, or Webpack build pipelines to the runtime shell.
3. ❌ Add external network requests on startup (updating filter lists is strictly on-demand or user-configured).
4. ❌ Add background polling timers with intervals under 1 second (except for active video playback ad skipping).
5. ❌ Allow unhandled exceptions or GDI handle leaks (always dispose brushes, pens, and context menus).
6. ❌ Bypass the two-tier tab suspension/hibernation architecture.

---

## ✅ Feature Addition Checklist

Before submitting or generating code for a new feature, verify:
- [ ] Does idle RAM remain under 150 MB?
- [ ] Does the change allocate heap memory in per-request or per-frame paths?
- [ ] Is the feature usable with keyboard shortcuts?
- [ ] Does the feature respect incognito mode (zero artifacts left on disk)?
- [ ] Does the UI conform to the dark-glass owner-drawn palette in `Theme.cs`?
