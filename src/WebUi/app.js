// Hallow Torrents web interface. Talks only to this server (/ui/api/...), with the session cookie and the X-CSRF-Token header.
// Torrent names and search results are put on the page as text (never as HTML).
(function () {
  'use strict';
  var csrf = null, tab = 'transfers', catFilter = '', cats = [], poll = null, lastList = null;
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function show(id, on) { $(id).classList.toggle('hidden', !on); }
  function bytes(n) { if (n == null || n < 0) return '?'; var u = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return (i ? n.toFixed(n < 10 ? 2 : 1) : n) + ' ' + u[i]; }
  function rate(n) { return bytes(n || 0) + '/s'; }
  function eta(s) { if (s == null) return ''; if (s < 60) return s + 's'; if (s < 3600) return Math.floor(s / 60) + 'm ' + (s % 60) + 's'; if (s < 86400) return Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm'; return Math.floor(s / 86400) + 'd'; }
  function msg(id, text, kind) { var m = $(id); m.textContent = text || ''; m.className = 'msg' + (kind ? ' ' + kind : ''); }

  function api(method, name, data) {
    var opt = { method: method, credentials: 'same-origin', headers: {} };
    if (method === 'POST') {
      if (csrf) opt.headers['X-CSRF-Token'] = csrf;
      if (data instanceof FormData) opt.body = data;
      else { opt.headers['Content-Type'] = 'application/x-www-form-urlencoded'; opt.body = new URLSearchParams(data || {}).toString(); }
    }
    return fetch('/ui/api/' + name, opt).then(function (r) {
      return r.json().catch(function () { return { ok: false, error: 'HTTP ' + r.status }; }).then(function (j) {
        if (r.status === 403 && name !== 'login' && /Sign in/.test(j.error || '')) signedOut();
        j._status = r.status; return j;
      });
    });
  }

  // ---- theme: auto (the device's setting) / light / dark, remembered on this device
  var themes = ['auto', 'light', 'dark'];
  function applyTheme(t) {
    if (t === 'auto') document.documentElement.removeAttribute('data-theme'); else document.documentElement.setAttribute('data-theme', t);
    var b = $('theme'); if (b) b.textContent = t.charAt(0).toUpperCase() + t.slice(1);
  }
  var theme = 'auto'; try { theme = localStorage.getItem('hallowTheme') || 'auto'; } catch (e) { }
  var qs = /[?&]theme=(light|dark|auto)/.exec(location.search); if (qs) theme = qs[1];
  applyTheme(theme);

  // ---- sign-in
  function signedOut() { csrf = null; if (poll) { clearInterval(poll); poll = null; } show('app', false); show('logout', false); show('vpn', false); show('connect', false); show('login', true); }
  function signedIn(token) {
    csrf = token; show('login', false); show('app', true); show('logout', true);
    api('GET', 'categories').then(function (c) { if (Array.isArray(c)) { cats = c; fillCats(); if (lastList) drawList(lastList); } });
    refresh(); if (!poll) poll = setInterval(function () { if (!document.hidden) refresh(); }, 2000);
  }
  function fillCats() {
    var s = $('addCat'); s.textContent = ''; s.appendChild(new Option('No category', ''));
    cats.forEach(function (c) { s.appendChild(new Option(c, c)); });
    var chips = $('catChips'); chips.textContent = '';
    [''].concat(cats).forEach(function (c) {
      var b = el('button', 'chip' + (c === catFilter ? ' on' : ''), c || 'All'); b.type = 'button';
      b.addEventListener('click', function () { catFilter = c; fillCats(); if (lastList) drawList(lastList); });
      chips.appendChild(b);
    });
  }

  // ---- status, list, stats
  function vpnState(locked, held, game) {
    var p = $('vpn'); show('vpn', true);
    if (locked) { p.className = 'pill amber'; p.textContent = 'VPN locked - torrents wait'; }
    else if (held === 'game') { p.className = 'pill lilac'; p.textContent = 'Paused for ' + (game || 'a game'); }
    else { p.className = 'pill green'; p.textContent = 'Running over the VPN'; }
    show('connect', !!locked); show('lockBanner', !!locked);
  }
  function refresh() {
    if (tab === 'transfers') api('GET', 'list').then(function (l) { if (l && l.torrents) { lastList = l; drawList(l); } });
    else api('GET', 'status').then(function (s) { if (s && s.version) vpnState(s.vpnLocked, s.held, s.game); });
    if (tab === 'stats') api('GET', 'stats').then(drawStats);
  }
  function drawList(l) {
    vpnState(l.vpnLocked, l.held, l.game);
    $('speeds').textContent = 'Down ' + rate(l.downBps) + '  ·  Up ' + rate(l.upBps) + '  ·  ' + l.count + ' torrent' + (l.count === 1 ? '' : 's');
    var box = $('list'); box.textContent = '';
    var items = l.torrents.filter(function (t) { return !catFilter || t.category.toLowerCase() === catFilter.toLowerCase(); });
    if (!items.length) { box.appendChild(el('div', 'empty', l.count ? 'Nothing in this category.' : 'No torrents yet - paste a magnet link above.')); return; }
    items.forEach(function (t) {
      var it = el('div', 'item'); it.appendChild(el('div', 'name', t.name || t.hash));
      var bar = el('div', 'bar' + (t.complete ? ' done' : '')); var fill = el('div'); fill.style.width = Math.round(t.progress * 1000) / 10 + '%'; bar.appendChild(fill); it.appendChild(bar);
      var meta = el('div', 'meta');
      var stCls = t.status === 'downloading' || t.status === 'seeding' ? 'green' : t.status === 'error' ? 'red' : t.status === 'waiting-vpn' || t.status === 'game' ? 'amber' : 'lilac';
      meta.appendChild(el('span', 'pill ' + stCls, t.state));
      meta.appendChild(el('span', null, (Math.round(t.progress * 1000) / 10) + '% of ' + bytes(t.size)));
      if (!t.complete) meta.appendChild(el('span', null, '↓ ' + rate(t.downBps)));
      meta.appendChild(el('span', null, '↑ ' + rate(t.upBps)));
      if (t.eta != null && t.downBps > 0) meta.appendChild(el('span', null, 'ETA ' + eta(t.eta)));
      meta.appendChild(el('span', null, t.seeds + ' seeds · ' + t.peers + ' peers · ratio ' + t.ratio));
      if (t.vpnLocked) meta.appendChild(el('span', null, 'waits for the VPN'));
      it.appendChild(meta);
      if (t.flagged) it.appendChild(el('div', 'flag', '⚠ Check before opening - see Hallow Torrents on the PC.'));
      var acts = el('div', 'acts');
      var pb = el('button', 'btn small', t.paused ? 'Resume' : 'Pause'); pb.type = 'button';
      pb.addEventListener('click', function () { api('POST', t.paused ? 'resume' : 'pause', { hash: t.hash }).then(refresh); });
      acts.appendChild(pb);
      var sel = el('select'); sel.setAttribute('aria-label', 'Category'); sel.appendChild(new Option('No category', ''));
      cats.forEach(function (c) { var o = new Option(c, c); if (c.toLowerCase() === (t.category || '').toLowerCase()) o.selected = true; sel.appendChild(o); });
      sel.addEventListener('change', function () { api('POST', 'category', { hash: t.hash, category: sel.value }).then(refresh); });
      acts.appendChild(sel); it.appendChild(acts); box.appendChild(it);
    });
  }
  function tile(k, v) { var t = el('div', 'card tile'); t.appendChild(el('div', 'k', k)); t.appendChild(el('div', 'v', v)); return t; }
  function hours(s) { return s < 3600 ? Math.round(s / 60) + ' min' : (s / 3600).toFixed(1) + ' h'; }
  function drawStats(s) {
    if (!s || !s.session) return; vpnState(s.vpnLocked, null, null);
    var g = $('stats'); g.textContent = '';
    g.appendChild(tile('Downloaded (session)', bytes(s.session.down))); g.appendChild(tile('Uploaded (session)', bytes(s.session.up)));
    g.appendChild(tile('Ratio (session)', String(s.session.ratio))); g.appendChild(tile('Downloaded (all time)', bytes(s.allTime.down)));
    g.appendChild(tile('Uploaded (all time)', bytes(s.allTime.up))); g.appendChild(tile('Ratio (all time)', String(s.allTime.ratio)));
    g.appendChild(tile('Time seeding', hours(s.allTime.seedSecs))); g.appendChild(tile('Waited for the VPN', hours(s.allTime.lockedSecs)));
    g.appendChild(tile('Peers blocked', String(s.allTime.blocked))); g.appendChild(tile('Torrents', s.torrents + ' (' + s.downloading + ' downloading)'));
    g.appendChild(tile('Speed now', '↓ ' + rate(s.downBps) + '  ↑ ' + rate(s.upBps)));
  }

  // ---- search (your own indexers, through Hallow Torrents)
  function drawSearch(r) {
    var pills = $('idxPills'); pills.textContent = ''; var box = $('results'); box.textContent = '';
    (r.indexers || []).forEach(function (s) {
      var cls = s.state === 'ok' ? 'green' : s.state === 'timeout' || s.state === 'locked' ? 'amber' : 'red';
      var p = el('span', 'pill ' + cls, s.name + '  ' + (s.state === 'ok' ? s.count + ' · ' + s.ms + ' ms' : s.state === 'locked' ? 'waiting for VPN' : s.state === 'timeout' ? 'timed out' : 'error'));
      if (s.error) p.title = s.error;
      pills.appendChild(p);
    });
    if ((r.indexers || []).some(function (s) { return s.local; })) pills.appendChild(el('span', 'pill lilac', 'Local server — not through VPN (private address)'));
    if (r.error) msg('searchMsg', r.error, 'err');
    else msg('searchMsg', r.total + ' results' + (r.total > r.count ? ' (best ' + r.count + ' shown)' : '') + ' in ' + (r.ms / 1000).toFixed(1) + ' s');
    (r.results || []).forEach(function (x) {
      var it = el('div', 'item'); it.appendChild(el('div', 'name', x.title));
      var meta = el('div', 'meta');
      if (x.flags && x.flags.length) meta.appendChild(el('span', 'pill red', 'Likely fake'));
      else { if (x.resolution) meta.appendChild(el('span', 'pill lilac', x.resolution)); if (x.inList) meta.appendChild(el('span', 'pill green', 'In your list')); }
      meta.appendChild(el('span', null, 'score ' + Math.round(x.score)));
      meta.appendChild(el('span', null, bytes(x.size)));
      meta.appendChild(el('span', null, (x.seeders < 0 ? '?' : x.seeders) + ' seeders'));
      if (x.indexer) meta.appendChild(el('span', null, x.indexer));
      if (x.source) meta.appendChild(el('span', null, x.source));
      it.appendChild(meta);
      (x.flags || []).forEach(function (f) { it.appendChild(el('div', 'flag', '⚠ ' + f)); });
      var acts = el('div', 'acts'); var sel = el('select'); sel.setAttribute('aria-label', 'Category'); sel.appendChild(new Option('Category: automatic', ''));
      cats.forEach(function (c) { sel.appendChild(new Option(c, c)); });
      var b = el('button', 'btn small', 'Add'); b.type = 'button';
      b.addEventListener('click', function () {
        b.disabled = true; b.textContent = 'Adding...';
        api('POST', 'search-add', { id: x.id, category: sel.value }).then(function (j) {
          b.textContent = j.ok ? (j.vpnLocked ? 'Added - waits for the VPN' : 'Added') : 'Add'; b.disabled = !!j.ok;
          if (!j.ok) msg('searchMsg', j.error || 'It could not be added.', 'err');
        });
      });
      acts.appendChild(sel); acts.appendChild(b); it.appendChild(acts); box.appendChild(it);
    });
  }

  // ---- wiring
  document.addEventListener('DOMContentLoaded', function () {
    applyTheme(theme);
    $('theme').addEventListener('click', function () { theme = themes[(themes.indexOf(theme) + 1) % 3]; try { localStorage.setItem('hallowTheme', theme); } catch (e) { } applyTheme(theme); });
    $('loginForm').addEventListener('submit', function (e) {
      e.preventDefault(); msg('loginMsg', 'Signing in...');
      api('POST', 'login', { username: $('user').value, password: $('pass').value }).then(function (j) {
        $('pass').value = '';
        if (j.ok) { msg('loginMsg', ''); signedIn(j.csrf); } else msg('loginMsg', j.error || 'Wrong username or password.', 'err');
      });
    });
    $('logout').addEventListener('click', function () { api('POST', 'logout').then(signedOut); });
    $('connect').addEventListener('click', function () {
      $('connect').disabled = true;
      api('POST', 'connect-vpn').then(function (j) { $('connect').disabled = false; msg('addMsg', j.ok ? 'Asked Hallow VPN to connect - torrents start once it is up.' : (j.error || 'Hallow VPN did not answer.'), j.ok ? 'good' : 'err'); });
    });
    Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (b) {
      b.addEventListener('click', function () {
        tab = b.getAttribute('data-tab');
        Array.prototype.forEach.call(document.querySelectorAll('.tab'), function (x) { x.classList.toggle('on', x === b); });
        ['transfers', 'search', 'stats'].forEach(function (t) { show('tab-' + t, t === tab); });
        refresh();
      });
    });
    $('addForm').addEventListener('submit', function (e) {
      e.preventDefault(); var f = $('file').files[0], m = $('magnet').value.trim(), c = $('addCat').value, req;
      if (f) { var fd = new FormData(); fd.append('file', f, f.name); fd.append('category', c); req = api('POST', 'add', fd); }
      else if (m) req = api('POST', 'add', { magnet: m, category: c });
      else { msg('addMsg', 'Paste a magnet link or pick a .torrent file.', 'err'); return; }
      msg('addMsg', 'Adding...');
      req.then(function (j) {
        if (j.ok) { $('magnet').value = ''; $('file').value = ''; msg('addMsg', 'Added: ' + (j.name || j.hash) + (j.vpnLocked ? ' - it starts when your VPN is connected.' : ''), 'good'); refresh(); }
        else msg('addMsg', j.error || 'It could not be added.', 'err');
      });
    });
    $('file').addEventListener('change', function () { var f = $('file').files[0]; if (f) msg('addMsg', f.name + ' - click Add.'); });
    $('pauseAll').addEventListener('click', function () { api('POST', 'pause', { hash: 'all' }).then(refresh); });
    $('resumeAll').addEventListener('click', function () { api('POST', 'resume', { hash: 'all' }).then(refresh); });
    $('searchForm').addEventListener('submit', function (e) {
      e.preventDefault(); var q = $('q').value.trim(); if (!q) return;
      msg('searchMsg', 'Searching your indexers...'); $('results').textContent = ''; $('idxPills').textContent = '';
      api('POST', 'search', { q: q, cat: $('sCat').value, n: 50 }).then(drawSearch);
    });
    api('GET', 'session').then(function (s) { if (s.signedIn) signedIn(s.csrf); else signedOut(); });
  });
})();
