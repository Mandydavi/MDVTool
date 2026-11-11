(function () {
  'use strict';

  // 防止同一页面重复注入
  if (window.__mdvInjected) return;
  window.__mdvInjected = true;

  const ID_PREFIX = 'mdv-';
  const PANEL_ID = ID_PREFIX + 'panel';
  const OVERLAY_BTN_CLASS = ID_PREFIX + 'overlay-btn';
  const BOUND_ATTR = 'data-mdv-bound';

  window.__mdvMedia = window.__mdvMedia || [];

  let seq = 0;
  let scheduled = false;
  const bound = [];

  function root() {
    return document.body || document.documentElement;
  }

  function send(url) {
    try {
      chrome.runtime.sendMessage({
        type: 'download',
        url: url || location.href,
        title: document.title || '',
        pageUrl: location.href
      });
    } catch (e) {
      console.warn('MDV Tool: 无法与扩展通信', e);
    }
  }

  function mediaUrl(media) {
    if (media.currentSrc) return media.currentSrc;
    if (media.src) return media.src;
    const src = media.querySelector('source[src]');
    return src ? src.src : location.href;
  }

  // 给 video / audio 叠加一个悬停才显现的悬浮按钮
  function bindMedia(media) {
    if (!media || media.getAttribute(BOUND_ATTR) === '1') return;
    media.setAttribute(BOUND_ATTR, '1');

    const id = ++seq;
    const btn = document.createElement('button');
    btn.id = ID_PREFIX + 'video-btn-' + id;
    btn.className = OVERLAY_BTN_CLASS;
    btn.type = 'button';
    btn.textContent = '下载';
    btn.style.cssText = [
      'position:fixed',
      'z-index:2147483647',
      'display:none',
      'margin:0',
      'padding:4px 10px',
      'font:12px/1.4 Arial,sans-serif',
      'color:#fff',
      'background:rgba(20,20,20,0.88)',
      'border:1px solid rgba(255,255,255,0.25)',
      'border-radius:6px',
      'cursor:pointer',
      'box-shadow:0 2px 8px rgba(0,0,0,0.45)'
    ].join(';') + ';';
    root().appendChild(btn);

    let hideTimer = null;
    function show() {
      if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
      const r = media.getBoundingClientRect();
      if (!r.width && !r.height) return;
      btn.style.left = Math.max(0, r.left + 8) + 'px';
      btn.style.top = Math.max(0, r.top + 8) + 'px';
      btn.style.display = 'block';
    }
    function scheduleHide() {
      if (hideTimer) clearTimeout(hideTimer);
      hideTimer = setTimeout(function () { btn.style.display = 'none'; }, 160);
    }

    media.addEventListener('mouseenter', show);
    media.addEventListener('mouseleave', scheduleHide);
    btn.addEventListener('mouseenter', show);
    btn.addEventListener('mouseleave', scheduleHide);
    btn.addEventListener('click', function (e) {
      e.preventDefault();
      e.stopPropagation();
      send(mediaUrl(media));
      btn.style.display = 'none';
    });

    bound.push({ media: media, btn: btn });
  }

  // 清理已脱离文档的媒体对应的悬浮按钮
  function cleanupOrphans() {
    for (let i = bound.length - 1; i >= 0; i--) {
      const item = bound[i];
      if (document.contains(item.media)) continue;
      try { item.btn.remove(); } catch (e) {}
      try { item.media.removeAttribute(BOUND_ATTR); } catch (e) {}
      bound.splice(i, 1);
    }
  }

  function shortName(u) {
    try {
      const p = new URL(u, location.href).pathname;
      return decodeURIComponent(p.split('/').pop() || '') || u;
    } catch (e) {
      return u;
    }
  }

  // 收集 performance 资源表里的 m3u8 / mpd 地址，去重后存进 window.__mdvMedia
  function collectResources() {
    let entries = [];
    try {
      entries = performance.getEntriesByType('resource') || [];
    } catch (e) {
      return;
    }

    const found = new Set(window.__mdvMedia);
    let changed = false;
    for (const entry of entries) {
      const name = entry.name || '';
      const path = name.split(/[?#]/)[0].toLowerCase();
      if (/\.(m3u8|mpd)$/.test(path) && !found.has(name)) {
        found.add(name);
        changed = true;
      }
    }
    if (!changed) return;

    window.__mdvMedia = Array.from(found);
    renderPanel();
  }

  // 固定右下角的小面板，逐条列出地址并带下载按钮
  function renderPanel() {
    const list = window.__mdvMedia || [];
    if (!list.length) return;

    let panel = document.getElementById(PANEL_ID);
    if (!panel) {
      panel = document.createElement('div');
      panel.id = PANEL_ID;
      panel.style.cssText = [
        'position:fixed',
        'right:12px',
        'bottom:12px',
        'z-index:2147483646',
        'max-width:340px',
        'max-height:210px',
        'overflow:auto',
        'padding:8px 10px',
        'font:12px/1.5 Arial,sans-serif',
        'color:#fff',
        'background:rgba(18,18,18,0.9)',
        'border:1px solid rgba(255,255,255,0.2)',
        'border-radius:8px',
        'box-shadow:0 4px 16px rgba(0,0,0,0.45)'
      ].join(';') + ';';
      const head = document.createElement('div');
      head.textContent = 'MDV Tool 嗅探到的媒体';
      head.style.cssText = 'font-weight:600;margin-bottom:6px;opacity:0.85;';
      panel.appendChild(head);
      root().appendChild(panel);
    }

    while (panel.childNodes.length > 1) panel.removeChild(panel.lastChild);

    list.forEach(function (u, i) {
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:center;gap:6px;margin:4px 0;';

      const label = document.createElement('span');
      label.textContent = shortName(u);
      label.title = u;
      label.style.cssText = 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;opacity:0.85;';

      const b = document.createElement('button');
      b.id = ID_PREFIX + 'panel-btn-' + i;
      b.type = 'button';
      b.textContent = '下载';
      b.style.cssText = 'flex:none;padding:2px 8px;font:12px/1.4 Arial,sans-serif;color:#fff;background:#2b6cb0;border:0;border-radius:5px;cursor:pointer;';
      b.addEventListener('click', function (e) {
        e.preventDefault();
        send(u);
      });

      row.appendChild(label);
      row.appendChild(b);
      panel.appendChild(row);
    });
  }

  function scan() {
    const nodes = document.querySelectorAll('video, audio');
    for (const m of nodes) bindMedia(m);
    cleanupOrphans();
    collectResources();
  }

  // 处理动态插入的 video / audio；用 rAF 合并回自己的 DOM 改动，避免回环
  const observer = new MutationObserver(function () {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(function () {
      scheduled = false;
      scan();
    });
  });
  observer.observe(document.documentElement || document.body, { childList: true, subtree: true });

  scan();
  window.addEventListener('load', scan);
  setTimeout(scan, 1500);
  setInterval(collectResources, 4000);
})();
