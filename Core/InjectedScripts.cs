using System.Text.Json;

namespace R1.Core;

/// <summary>
/// Script injected at document start in every frame: cosmetic CSS, anti-adblock
/// neutralisation (bait spoofing, detector stubs, overlay removal) and the
/// host handshake used to fetch domain-specific cosmetic rules.
/// </summary>
public static class InjectedScripts
{
    // CSS that keeps classic ad-bait elements "present" so detection scripts see
    // a non-zero box. Applied after the cosmetic sheet so it wins !important ties.
    private const string BaitCss = """
[class*="adsbox"],[id*="adsbox"],[class*="ad-banner"],[id*="ad-banner"],.adsbygoogle,[id^="google_ads"],[class*="ad-placeholder"],[id*="ad-slot"],[id*="div-gpt-ad"],[class*="ad-container"],[class*="banner-ad"],[id*="banner-ad"]{display:block!important;visibility:visible!important;opacity:.01!important;height:2px!important;width:2px!important;position:absolute!important;left:-9999px!important;top:-9999px!important;overflow:hidden!important;pointer-events:none!important}
""";

    private const string Js = """
(function () {
  if (window.__r1init) return;
  window.__r1init = 1;

  var config = __CONFIG__;
  var enabled = config.enabled && !config.whitelist.some(function (h) { return location.hostname === h || location.hostname.endsWith('.' + h); });
  var anti = enabled && config.anti;
  window.__r1configure = function (on, antiOn) { enabled = on; anti = antiOn; };
  var BAIT = __BAIT__;
  var GENERIC = __CSS__;
  var styleEls = [];
  var adopted = [];

  function makeSheet(css) {
    try { var s = new CSSStyleSheet(); s.replaceSync(css); return s; } catch (e) { return null; }
  }

  function clearCss() {
    if (adopted.length) { try { document.adoptedStyleSheets = document.adoptedStyleSheets.filter(function (s) { return adopted.indexOf(s) < 0; }); } catch (e) { } }
    for (var i = 0; i < styleEls.length; i++) { try { styleEls[i].remove(); } catch (e) { } }
    styleEls = [];
    adopted = [];
  }

  function applyCss(css) {
    clearCss();
    if (!enabled) return;
    var parts = [];
    if (css) parts.push(css);
    if (anti) parts.push(BAIT);

    var sheets = [];
    for (var i = 0; i < parts.length; i++) {
      var s = makeSheet(parts[i]);
      if (!s) { sheets = null; break; }
      sheets.push(s);
    }
    if (sheets && sheets.length) {
      try { document.adoptedStyleSheets = document.adoptedStyleSheets.concat(sheets); adopted = sheets; return; } catch (e) { }
    }

    var host = document.head || document.documentElement;
    if (!host) {
      document.addEventListener('DOMContentLoaded', function () { applyCss(css); }, { once: true });
      return;
    }
    for (var j = 0; j < parts.length; j++) {
      var el = document.createElement('style');
      el.setAttribute('data-r1', '');
      el.textContent = parts[j];
      host.appendChild(el);
      styleEls.push(el);
    }
  }

  // Allow deliberate links and same-origin workflows; reject click-hijacked popunders.
  var intent = null;
  var buttonIntent = 0;
  document.addEventListener('click', function (event) {
    if (!event.isTrusted) return;
    var path = event.composedPath ? event.composedPath() : [];
    var link = path.find(function (n) { return n && n.tagName === 'A' && n.href; });
    intent = link ? { url: link.href, at: Date.now() } : null;
    var btn = path.find(function (n) {
      if (!n || !n.tagName) return false;
      var tag = n.tagName;
      var role = n.getAttribute ? n.getAttribute('role') : '';
      return tag === 'BUTTON' || (tag === 'INPUT' && (n.type === 'button' || n.type === 'submit')) || role === 'button';
    });
    if (btn) buttonIntent = Date.now();
  }, true);

  var isAuthOrPayment = function (u) {
    var h = (u.hostname || '').toLowerCase();
    var p = (u.pathname || '').toLowerCase();
    return /(auth|login|signin|oauth|account|checkout|pay|paypal|stripe|google|apple|github|microsoft)/i.test(h + p);
  };

  var nativeOpen = window.open;
  window.open = function (url, target, features) {
    if (enabled) {
      var dest;
      try { dest = new URL(url || 'about:blank', location.href); } catch (_) { return null; }
      if (dest.protocol !== 'http:' && dest.protocol !== 'https:') return null;
      var deliberate = intent && Date.now() - intent.at < 800 && intent.url === dest.href;
      var workflow = navigator.userActivation && navigator.userActivation.isActive && dest.origin === location.origin;
      var authWorkflow = buttonIntent && Date.now() - buttonIntent < 800 && navigator.userActivation && navigator.userActivation.isActive && isAuthOrPayment(dest);
      if (!deliberate && !workflow && !authWorkflow) return null;
      intent = null;
      buttonIntent = 0;
    }
    return nativeOpen.call(window, url, target, features);
  };

  // ---------- YouTube ad skipper & protection ----------
  function handleYouTube() {
    if (!enabled || !/youtube\.com$/i.test(location.hostname)) return;
    try {
      var video = document.querySelector('.html5-video-player video');
      var adPlayer = document.querySelector('.ad-showing, .ad-interrupting');
      if (adPlayer && video) {
        video.muted = true;
        video.playbackRate = 16;
        if (isFinite(video.duration) && video.duration > 0) {
          video.currentTime = video.duration;
        }
      }
      var skipBtn = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, .ytp-ad-skip-button-text');
      if (skipBtn) skipBtn.click();

      var enforcement = document.querySelector('ytd-enforcement-message-view-model, tp-yt-paper-dialog:has(#feedback)');
      if (enforcement) {
        enforcement.remove();
        var backdrops = document.querySelectorAll('tp-yt-iron-overlay-backdrop');
        for (var b = 0; b < backdrops.length; b++) backdrops[b].remove();
        unfreeze();
        if (video && video.paused) video.play();
      }
    } catch (e) { }
  }

  // ---------- anti ad-block ----------
  var DETECT = /(ad[\s-]?block|adblocker|ad blocker|disable.{0,24}(block|filter)|whitelist|allow.{0,12}ads|turn off.{0,16}(block|filter))/i;

  function unfreeze() {
    try {
      var d = document.documentElement, b = document.body;
      if (d) { d.style.setProperty('overflow', 'auto', 'important'); d.style.setProperty('filter', 'none', 'important'); }
      if (b) {
        b.style.setProperty('overflow', 'auto', 'important');
        b.style.setProperty('filter', 'none', 'important');
        b.style.setProperty('pointer-events', 'auto', 'important');
      }
    } catch (e) { }
  }

  function isOverlay(cs, r) {
    var pos = cs.position;
    if (pos !== 'fixed' && pos !== 'absolute' && pos !== 'sticky') return false;
    if (r.width < innerWidth * 0.38 || r.height < innerHeight * 0.26) return false;
    var z = parseInt(cs.zIndex, 10);
    if (isNaN(z)) z = 0;
    return z >= 10 || pos === 'fixed';
  }

  function sweep() {
    if (enabled && /youtube\.com$/i.test(location.hostname)) handleYouTube();
    if (!anti || !document.body) return;
    var sel = '[class*="adblock"],[id*="adblock"],[class*="anti-ad"],[class*="antiad"],[class*="overlay"],[class*="modal"],[class*="popup"],[class*="dialog"],[class*="paywall"],[class*="notice"],[class*="warning"],[class*="detect"]';
    var nodes;
    try { nodes = document.querySelectorAll(sel); } catch (e) { return; }
    var hit = 0;
    for (var i = 0; i < nodes.length && i < 300; i++) {
      var el = nodes[i];
      if (el.__r1keep) continue;
      var cs; try { cs = getComputedStyle(el); } catch (e) { continue; }
      if (cs.display === 'none' || cs.visibility === 'hidden') continue;
      var r = el.getBoundingClientRect();
      if (!isOverlay(cs, r)) continue;
      var txt = (el.innerText || '').slice(0, 400);
      if (!DETECT.test(txt)) continue;
      try { el.remove(); hit++; } catch (e) { }
      unfreeze();
    }
    if (hit) {
      var kids = document.body.children;
      for (var k = kids.length - 1; k >= 0; k--) {
        var c = kids[k];
        if (!c || c.nodeType !== 1) continue;
        var s2; try { s2 = getComputedStyle(c); } catch (e) { continue; }
        if (s2.position !== 'fixed') continue;
        var r2 = c.getBoundingClientRect();
        var z2 = parseInt(s2.zIndex, 10); if (isNaN(z2)) z2 = 0;
        if (r2.width >= innerWidth * 0.9 && r2.height >= innerHeight * 0.9 && z2 >= 100) {
          try { c.remove(); } catch (e) { }
        }
      }
    }
  }

  var sweepTimer = 0;
  function scheduleSweep() {
    if (sweepTimer) return;
    sweepTimer = setTimeout(function () { sweepTimer = 0; sweep(); }, 350);
  }

  function boot() {
    if (!document.body) return;
    try {
      new MutationObserver(scheduleSweep).observe(document.body, { childList: true, subtree: true });
    } catch (e) { }
    if (/youtube\.com$/i.test(location.hostname)) setInterval(handleYouTube, 250);
    sweep();
    setTimeout(sweep, 1200);
    setTimeout(sweep, 3500);
  }

  function def(name, val) {
    if (!anti) return;
    try {
      Object.defineProperty(window, name, { configurable: true, get: function () { return val; }, set: function () { } });
    } catch (e) { try { window[name] = val; } catch (e2) { } }
  }

  def('canRunAds', true);
  def('canShowAds', true);
  def('adBlockDetected', false);
  def('adblockDetected', false);
  def('adBlockEnabled', false);
  def('isAdBlockActive', false);
  def('adsEnabled', true);

  try {
    if (anti && !window.adsbygoogle) window.adsbygoogle = { push: function () { }, loaded: true, u9: {}, u7: {} };
  } catch (e) { }

  function inert() {
    var o = {
      options: { set: function () { return o; } },
      onDetected: function () { return o; },
      onNotDetected: function (cb) { try { cb && cb(); } catch (e) { } return o; },
      on: function () { return o; },
      one: function () { return o; },
      off: function () { return o; },
      check: function () { return o; },
      detect: function () { return o; },
      setOption: function () { return o; },
      clearEventLog: function () { return o; },
      onDetectedOnce: function () { return o; }
    };
    return o;
  }

  function stub(name) {
    if (!anti) return;
    try { Object.defineProperty(window, name, { configurable: true, value: function () { return inert(); } }); } catch (e) { }
  }
  stub('FuckAdBlock');
  stub('BlockAdBlock');
  stub('AdBlockDetector');

  try {
    var nativeAlert = window.alert;
    if (nativeAlert) {
      window.alert = function (msg) {
        if (anti && DETECT.test(String(msg || ''))) return;
        return nativeAlert.apply(window, arguments);
      };
    }
  } catch (e) { }

  // WebRTC local IP leak shield: prevent trackers from harvesting local Wi-Fi IP
  try {
    if (enabled && window.RTCPeerConnection) {
      var OrigPC = window.RTCPeerConnection;
      var patch = function (pc) {
        var origOnIce = null;
        try {
          Object.defineProperty(pc, 'onicecandidate', {
            configurable: true,
            get: function () { return origOnIce; },
            set: function (fn) {
              origOnIce = typeof fn === 'function' ? function (e) {
                if (e && e.candidate && e.candidate.candidate && e.candidate.candidate.indexOf('typ host') !== -1) {
                  return;
                }
                return fn.apply(this, arguments);
              } : fn;
            }
          });
        } catch (_) { }
      };
      var origCreateOffer = OrigPC.prototype.createOffer;
      if (origCreateOffer) {
        OrigPC.prototype.createOffer = function () {
          patch(this);
          return origCreateOffer.apply(this, arguments);
        };
      }
    }
  } catch (e) { }

  // ---------- bootstrap ----------
  try { window.__r1apply = applyCss; } catch (e) { }

  var isTop = true;
  try { isTop = window.top === window; } catch (e) { isTop = false; }

  applyCss(isTop ? GENERIC : '');

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true });
  else boot();

  if (isTop) {
    try {
      chrome.webview.addEventListener('message', function (ev) {
        var d = ev.data;
        if (d && d.r1 === 'css') applyCss(d.css || '');
      });
      chrome.webview.postMessage({ r1: 'cos', host: location.hostname });
    } catch (e) { }
  }
})();
""";

    public static string Build(FilterEngine engine, Settings settings, string startHost)
    {
        // Domain CSS is delivered by the host handshake. Keeping it out of the
        // document-start script avoids duplicating a large stylesheet in every frame.
        const string css = "";
        return Js
            .Replace("__CONFIG__", JsonSerializer.Serialize(new { enabled = settings.AdblockEnabled, anti = settings.AntiAdblockEnabled, whitelist = settings.Whitelist.Select(h => h.ToLowerInvariant()).ToArray() }))
            .Replace("__BAIT__", JsonSerializer.Serialize(BaitCss))
            .Replace("__CSS__", JsonSerializer.Serialize(css));
    }
}
