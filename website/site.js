// WinMux homepage: the window in the hero.
// A small, honest model of WinMux's layout tree (splits, tab groups, leaves) drawn with
// positioned elements, and the session file it would write, in WinMux's real TOML format.
// Without this script the page shows a static three-pane layout and a sample of the file.
(function () {
  'use strict';

  var root = document.querySelector('[data-wm]');
  if (!root) return;

  var body = root.querySelector('[data-wm-body]');
  var tomlEl = document.querySelector('[data-toml]');
  var savedEl = document.querySelector('[data-toml-saved]');
  var caption = document.getElementById('demo-caption');
  var statusL = root.querySelector('[data-status-left]');
  var statusR = root.querySelector('[data-status-right]');
  var tools = root.querySelectorAll('[data-act]');

  var STRIP = 30;      // px: tab strips are reserved geometry, carved out of the stack's rect
  var MAX_PANES = 6;
  var CWD = 'C:\\src\\winmux';

  var KINDS = {
    terminal: { cls: 'pane-term', dot: 'k-term' },
    'file-browser': { cls: 'pane-files', dot: 'k-files' },
    'foreign-app': { cls: 'pane-app', dot: 'k-app' },
    empty: { cls: 'pane-empty', dot: 'k-empty' }
  };

  // What an empty pane offers. Shells are the seeded profiles; the editor stands for any
  // application found through the Start Menu; the viewer for a window that is already open.
  var LAUNCH = [
    { id: 'cmd', label: 'Command Prompt', kind: 'terminal', title: 'cmd', program: 'C:\\WINDOWS\\system32\\cmd.exe', shell: 'cmd' },
    { id: 'pwsh', label: 'PowerShell 7', kind: 'terminal', title: 'pwsh', program: 'C:\\Program Files\\PowerShell\\7\\pwsh.exe', shell: 'pwsh' },
    { id: 'wsl', label: 'WSL', kind: 'terminal', title: 'wsl', program: 'C:\\WINDOWS\\system32\\wsl.exe', shell: 'bash' },
    { id: 'files', label: 'File browser', kind: 'file-browser', title: 'files' },
    { id: 'editor', label: 'Editor', kind: 'foreign-app', title: 'notes.txt', program: 'C:\\Program Files\\Editor\\editor.exe', note: 'from the Start Menu' },
    { id: 'adopt', label: 'Open window…', kind: 'foreign-app', title: 'diagram.png', program: 'C:\\Program Files\\Viewer\\viewer.exe', note: 'adopted, already running', adopted: true }
  ];

  // ---------- model ----------

  var seq = 0;
  var panes = {};
  function pane(spec) {
    var id = 'p' + (++seq);
    panes[id] = {
      id: id, kind: spec.kind, title: spec.title, program: spec.program || null,
      shell: spec.shell || null, note: spec.note || null, adopted: !!spec.adopted,
      cwd: CWD, hung: false, restored: false, script: spec.script || null
    };
    return id;
  }
  function leaf(p) { return { t: 'leaf', pane: p }; }

  var tree, focused, isClosed = false;

  function reset() {
    seq = 0; panes = {};
    var a = pane({ kind: 'terminal', title: 'build', program: 'C:\\WINDOWS\\system32\\cmd.exe', shell: 'cmd', script: 'build' });
    var b = pane({ kind: 'file-browser', title: 'files' });
    var c = pane({ kind: 'foreign-app', title: 'notes.txt', program: 'C:\\Program Files\\Editor\\editor.exe', note: 'from the Start Menu' });
    tree = { t: 'split', dir: 'columns', ratios: [0.5, 0.5], kids: [leaf(a), { t: 'split', dir: 'rows', ratios: [0.5, 0.5], kids: [leaf(b), leaf(c)] }] };
    focused = a;
  }

  // Find the leaf holding a pane, with its parent and index.
  function locate(id, node, parent, index) {
    node = node || tree;
    if (node.t === 'leaf') return node.pane === id ? { node: node, parent: parent || null, index: index || 0 } : null;
    for (var i = 0; i < node.kids.length; i++) {
      var r = locate(id, node.kids[i], node, i);
      if (r) return r;
    }
    return null;
  }
  function parentOf(target, node) {
    node = node || tree;
    if (node.t === 'leaf') return null;
    for (var i = 0; i < node.kids.length; i++) {
      if (node.kids[i] === target) return { parent: node, index: i };
      var r = parentOf(target, node.kids[i]);
      if (r) return r;
    }
    return null;
  }
  function replace(oldNode, newNode) {
    if (oldNode === tree) { tree = newNode; return; }
    var p = parentOf(oldNode);
    p.parent.kids[p.index] = newNode;
  }
  function leaves(node, out) {
    out = out || [];
    node = node || tree;
    if (node.t === 'leaf') out.push(node.pane);
    else node.kids.forEach(function (k) { leaves(k, out); });
    return out;
  }
  function count() { return leaves().length; }
  // The pane a node shows: the active tab of a stack, the first child of a split.
  function activeLeaf(node) {
    if (node.t === 'leaf') return node.pane;
    return activeLeaf(node.t === 'stack' ? node.kids[node.active] : node.kids[0]);
  }

  // ---------- actions (the same names WinMux uses) ----------

  var words = {
    'split-columns': 'right of', 'split-rows': 'below'
  };

  function split(dir) {
    if (count() >= MAX_PANES) return say('This illustration stops at six panes. WinMux does not.');
    var at = locate(focused);
    var fresh = pane({ kind: 'empty', title: 'empty' });
    var p = at.parent;
    if (p && p.t === 'split' && p.dir === dir) {
      var half = p.ratios[at.index] / 2;
      p.ratios.splice(at.index, 1, half, half);
      p.kids.splice(at.index + 1, 0, leaf(fresh));
    } else {
      replace(at.node, { t: 'split', dir: dir, ratios: [0.5, 0.5], kids: [at.node, leaf(fresh)] });
    }
    focused = fresh;
    changed('split-' + dir, 'A new, empty pane ' + words['split-' + dir] + ' the focused one. An empty pane is a real state: pick what goes in it now, or later.');
  }

  function newTab() {
    if (count() >= MAX_PANES) return say('This illustration stops at six panes. WinMux does not.');
    var at = locate(focused);
    var fresh = pane({ kind: 'empty', title: 'empty' });
    if (at.parent && at.parent.t === 'stack') {
      at.parent.kids.splice(at.index + 1, 0, leaf(fresh));
      at.parent.active = at.index + 1;
    } else {
      replace(at.node, { t: 'stack', active: 1, kids: [at.node, leaf(fresh)] });
    }
    focused = fresh;
    changed('new-tab', 'The pane became a tab group. Its strip is carved out of the layout, so it can never be covered by an application window. Tab groups nest anywhere.');
  }

  function closePane() {
    var at = locate(focused);
    if (!at.parent) {
      if (panes[focused].kind === 'empty') return say('The last pane stays, empty, ready for whatever you put in it.');
      delete panes[focused];
      var e = pane({ kind: 'empty', title: 'empty' });
      tree = leaf(e); focused = e;
      return changed('close-pane', 'Closed. Only that pane\'s process went; the window stays with one empty pane.');
    }
    var p = at.parent;
    var wasApp = panes[focused].kind === 'foreign-app';
    p.kids.splice(at.index, 1);
    if (p.t === 'split') {
      var gone = p.ratios.splice(at.index, 1)[0];
      var n = Math.max(0, Math.min(at.index, p.ratios.length - 1));
      p.ratios[n] += gone;
    } else {
      // The focused pane is always the active tab, so its left neighbour takes over.
      p.active = Math.max(0, at.index - 1);
    }
    delete panes[focused];
    var next = p.t === 'stack' ? p.kids[p.active] : p.kids[Math.min(at.index, p.kids.length - 1)];
    if (p.kids.length === 1) replace(p, p.kids[0]);
    focused = activeLeaf(next);
    changed('close-pane', wasApp
      ? 'Closed. The application\'s window was handed back first, with its original parent, styles and position, then asked to close.'
      : 'Closed. Only that pane\'s process tree went; its neighbour took the space.');
  }

  function freeze() {
    var target = panes[focused] && panes[focused].kind === 'foreign-app' ? panes[focused]
      : visibleLeaves().map(function (id) { return panes[id]; }).filter(function (p) { return p.kind === 'foreign-app'; })[0];
    if (!target) return say('No application pane is on screen. Split, then choose Editor, and try again.');
    target.hung = !target.hung;
    render();
    say(target.hung
      ? 'The application stopped responding. Its own pane host is stuck waiting for it; WinMux is not. Click the other panes, split, switch tabs: everything still works.'
      : 'The application is responding again.');
  }

  function restart() {
    if (isClosed) return;
    isClosed = true;
    var apps = leaves().filter(function (id) { return panes[id].kind === 'foreign-app'; }).length;
    body.innerHTML = '';
    var c = el('div', 'closed');
    c.innerHTML = '<b>WinMux is closed.</b><span>The layout was already on disk; nothing was saved on the way out.' +
      (apps ? ' ' + apps + (apps === 1 ? ' application window was' : ' application windows were') + ' handed back to the desktop, still running.' : '') +
      '</span><code>session.toml · ' + count() + (count() === 1 ? ' pane' : ' panes') + '</code>';
    var b = el('button', 'btn btn-primary btn-small');
    b.type = 'button'; b.textContent = 'Start WinMux';
    b.addEventListener('click', reopen);
    c.appendChild(b);
    body.appendChild(c);
    statusL.textContent = 'not running';
    statusR.textContent = '';
    setTools();
    say('Closed. Everything you see in the session file is what comes back.');
    b.focus();
  }

  function reopen() {
    isClosed = false;
    Object.keys(panes).forEach(function (id) {
      var p = panes[id];
      p.hung = false;
      if (p.kind === 'terminal') { p.restored = true; p.script = null; }
      if (p.kind === 'foreign-app') p.reattached = true;
    });
    render(true);
    say('Back: the same tree and tabs, each shell started again in its saved folder. Shells are new processes with empty scrollback, because process state cannot be restored. Applications that kept running were reattached after their window, process and start time matched.');
  }

  function fill(spec) {
    var p = panes[focused];
    p.kind = spec.kind; p.title = spec.title; p.program = spec.program || null;
    p.shell = spec.shell || null; p.note = spec.note || null; p.adopted = !!spec.adopted;
    p.script = spec.kind === 'terminal' ? spec.shell : null;
    changed('fill', spec.kind === 'terminal'
      ? spec.label + ' started in ' + CWD + ', on its own ConPTY.'
      : spec.kind === 'file-browser'
        ? 'A file browser, opened at the same folder. Drag files between panes, or open a terminal at the selection.'
        : spec.adopted
          ? 'An open window, adopted into the pane. WinMux recorded how to find it again.'
          : 'An application from the Start Menu, hosted by its own WinMux.PaneHost process. It is embedded if it allows it, attached alongside if not.');
  }

  // ---------- drawing ----------

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function place(e, r) {
    e.style.left = r.x * 100 + '%';
    e.style.width = r.w * 100 + '%';
    e.style.top = 'calc(' + r.y * 100 + '% + ' + r.py + 'px)';
    e.style.height = 'calc(' + r.h * 100 + '% + ' + r.ph + 'px)';
  }

  var visibleSet = {};
  function visible(id) { return !!visibleSet[id]; }
  function visibleLeaves() { return leaves().filter(visible); }

  // Lay the tree out into rectangles. Percentages for the proportional part and a pixel term
  // for the reserved strips, so the result is exact at every window size.
  function lay(node, r, out) {
    if (node.t === 'leaf') { out.panes.push({ id: node.pane, r: r }); return; }
    if (node.t === 'stack') {
      out.strips.push({ node: node, r: { x: r.x, y: r.y, w: r.w, h: 0, py: r.py, ph: STRIP } });
      lay(node.kids[node.active], { x: r.x, y: r.y, w: r.w, h: r.h, py: r.py + STRIP, ph: r.ph - STRIP }, out);
      return;
    }
    var acc = 0;
    node.kids.forEach(function (k, i) {
      var f = node.ratios[i];
      var c = node.dir === 'columns'
        ? { x: r.x + r.w * acc, y: r.y, w: r.w * f, h: r.h, py: r.py, ph: r.ph }
        : { x: r.x, y: r.y + r.h * acc, w: r.w, h: r.h * f, py: r.py + r.ph * acc, ph: r.ph * f };
      acc += f;
      lay(k, c, out);
    });
  }

  function headFor(p) {
    var h = el('div', 'pane-head');
    var d = el('span', 'dot ' + KINDS[p.kind].dot);
    h.appendChild(d);
    var label = p.kind === 'terminal' ? p.title + ' · ' + p.shell
      : p.kind === 'file-browser' ? 'files · ' + p.cwd
      : p.kind === 'foreign-app' ? p.title + (p.adopted ? ' · Viewer' : ' · Editor')
      : 'empty pane';
    h.appendChild(document.createTextNode(label));
    if (p.kind === 'foreign-app') h.appendChild(el('span', 'app-host', 'PaneHost'));
    return h;
  }

  function prompt(p) {
    return p.shell === 'pwsh' ? 'PS ' + p.cwd + '>' : p.shell === 'bash' ? 'you@desk:~/src/winmux$' : p.cwd + '>';
  }

  function termFor(p) {
    var pre = el('pre', 'term');
    var P = '<span class="p">' + esc(prompt(p)) + '</span> ';
    var lines = [];
    if (p.restored) {
      lines.push('<span class="d">restored in ' + esc(p.shell === 'bash' ? '~/src/winmux' : p.cwd) + ' · new process, no scrollback</span>');
    } else if (p.script === 'build') {
      lines.push(P + 'dotnet build -c Release');
      lines.push('<span class="d">  WinMux.Core    -&gt; bin\\Release\\net10.0</span>');
      lines.push('<span class="d">  WinMux.Shell   -&gt; bin\\x64\\Release</span>');
      lines.push('<span class="g">Build succeeded.</span> 0 warnings, 0 errors');
    } else if (p.script === 'pwsh') {
      lines.push(P + 'git status --short');
      lines.push('<span class="w"> M</span> website/index.html');
    } else if (p.script === 'bash') {
      lines.push(P + 'uname -sr');
      lines.push('Linux 6.6.87-microsoft-standard-WSL2');
    }
    lines.push(P + '<span class="cur"></span>');
    pre.innerHTML = lines.join('\n');
    return pre;
  }

  function filesFor() {
    var ul = el('ul', 'files');
    [['docs', 1], ['WinMux.Core', 1], ['WinMux.PaneHost', 1], ['WinMux.Shell', 1], ['website', 1], ['README.md', 0], ['session.toml', 0]].forEach(function (f, i) {
      var li = el('li', (f[1] ? 'd' : '') + (i === 4 ? ' sel' : ''), f[0]);
      ul.appendChild(li);
    });
    return ul;
  }

  function appFor(p) {
    var app = el('div', 'app');
    var menu = el('div', 'app-menu');
    ['File', 'Edit', 'View', 'Help'].forEach(function (m) { menu.appendChild(el('span', null, m)); });
    app.appendChild(menu);
    var doc = el('div', 'app-doc');
    var text = p.adopted
      ? ['<p class="h">diagram.png</p>', '<p>1280 × 720 · 100%</p>']
      : ['<p class="h">Release checklist</p>', '<p>- tag v0.9.0</p>', '<p>- publish checksums.txt</p>', '<p>- update the website</p>'];
    if (p.reattached) text.push('<p class="restored-note">reattached: same window, same process</p>');
    doc.innerHTML = text.join('');
    app.appendChild(doc);
    return app;
  }

  function launcherFor() {
    var l = el('div', 'launch');
    l.appendChild(el('p', 'launch-title', 'What goes in this pane?'));
    l.appendChild(el('p', 'launch-sub', 'Profiles, installed applications and open windows'));
    var ul = el('ul');
    LAUNCH.forEach(function (spec) {
      var li = el('li');
      var b = el('button');
      b.type = 'button';
      b.appendChild(el('span', 'dot ' + KINDS[spec.kind].dot));
      b.appendChild(document.createTextNode(spec.label));
      b.addEventListener('click', function (ev) { ev.stopPropagation(); fill(spec); });
      li.appendChild(b);
      ul.appendChild(li);
    });
    l.appendChild(ul);
    return l;
  }

  function hungFor(p) {
    var h = el('div', 'hung');
    h.innerHTML = '<b>' + esc(p.title) + ' is not responding</b><span>Its pane host is waiting. WinMux is not.</span>';
    var a = el('div', 'hung-actions');
    var wait = el('button', null, 'Wait');
    wait.type = 'button';
    wait.addEventListener('click', function (ev) { ev.stopPropagation(); p.hung = false; render(); say('It came back. Nothing else noticed.'); });
    var detach = el('button', null, 'Detach to desktop');
    detach.type = 'button';
    detach.addEventListener('click', function (ev) {
      ev.stopPropagation();
      focused = p.id;
      p.hung = false;
      closePane();
      say('Detached: the window went back to the desktop with its original parent, styles and position. It is still running there, still frozen, and no longer your problem.');
    });
    a.appendChild(wait); a.appendChild(detach);
    h.appendChild(a);
    return h;
  }

  function render(entering) {
    if (isClosed) return;
    var out = { panes: [], strips: [] };
    lay(tree, { x: 0, y: 0, w: 1, h: 1, py: 0, ph: 0 }, out);
    visibleSet = {};
    out.panes.forEach(function (it) { visibleSet[it.id] = true; });
    if (!visible(focused)) focused = out.panes[0].id;

    body.innerHTML = '';
    out.strips.forEach(function (s) {
      var strip = el('div', 'strip');
      strip.setAttribute('role', 'tablist');
      place(strip, s.r);
      s.node.kids.forEach(function (k, i) {
        var first = leaves(k)[0];
        var p = panes[first];
        var b = el('button');
        b.type = 'button';
        b.setAttribute('role', 'tab');
        b.setAttribute('aria-selected', i === s.node.active ? 'true' : 'false');
        b.appendChild(el('span', 'dot ' + KINDS[p.kind].dot));
        b.appendChild(document.createTextNode(p.kind === 'empty' ? 'empty' : p.title));
        b.addEventListener('click', function () {
          s.node.active = i; focused = first; render();
          say('Switched tab. Every tab\'s process keeps running while it is hidden; a program that needs you can raise a notification.');
        });
        strip.appendChild(b);
      });
      body.appendChild(strip);
    });

    out.panes.forEach(function (it) {
      var p = panes[it.id];
      var wrap = el('div', 'pane ' + KINDS[p.kind].cls + (it.id === focused ? ' is-focused' : ''));
      place(wrap, it.r);
      var inner = el('div', 'pane-in' + (entering ? ' enter' : ''));
      inner.setAttribute('role', 'group');
      inner.setAttribute('aria-label', (p.kind === 'empty' ? 'Empty pane' : p.title) + (it.id === focused ? ', focused' : ''));
      inner.tabIndex = 0;
      inner.appendChild(headFor(p));
      if (p.kind === 'terminal') inner.appendChild(termFor(p));
      else if (p.kind === 'file-browser') inner.appendChild(filesFor());
      else if (p.kind === 'foreign-app') inner.appendChild(appFor(p));
      else inner.appendChild(launcherFor());
      if (p.hung) inner.appendChild(hungFor(p));
      var focus = function () {
        if (focused === it.id) return;
        focused = it.id;
        render();
        var now = body.querySelector('.is-focused .pane-in');
        if (now) now.focus({ preventScroll: true });
      };
      inner.addEventListener('click', focus);
      inner.addEventListener('keydown', function (ev) { if (ev.key === 'Enter' || ev.key === ' ') { if (ev.target === inner) { ev.preventDefault(); focus(); } } });
      wrap.appendChild(inner);
      body.appendChild(wrap);
    });

    var n = count();
    statusL.textContent = n + (n === 1 ? ' pane' : ' panes') + ' · focused: ' + (panes[focused].kind === 'empty' ? 'empty' : panes[focused].title);
    statusR.textContent = panes[focused].shell === 'bash' ? '~/src/winmux' : CWD;
    setTools();
    writeToml();
  }

  function setTools() {
    tools.forEach(function (b) {
      var act = b.getAttribute('data-act');
      if (act === 'restart') { b.disabled = isClosed; return; }
      b.disabled = isClosed;
      if (act === 'freeze') {
        var anyHung = Object.keys(panes).some(function (id) { return panes[id].hung && visible(id); });
        b.setAttribute('aria-pressed', anyHung ? 'true' : 'false');
      }
    });
  }

  // ---------- the session file ----------

  function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
  function str(s) { return '<span class="s">\'' + esc(s) + '\'</span>'; }
  function kv(k, v, w) { var pad = new Array(Math.max(1, w - k.length + 1)).join(' '); return '<span class="k">' + k + '</span>' + pad + ' = ' + v; }

  var lastToml = [];
  function writeToml() {
    var nodes = [];
    var ids = new Map();
    (function walk(node) {
      ids.set(node, 'n' + nodes.length);
      nodes.push(node);
      if (node.t !== 'leaf') node.kids.forEach(walk);
    })(tree);

    var L = [];
    L.push(kv('version', '1', 7));
    L.push('');
    L.push('[[<span class="t">windows</span>]]');
    L.push(kv('root', str('n0'), 7));
    L.push(kv('focused', str(focused), 7));
    nodes.forEach(function (node) {
      L.push('');
      L.push('[[<span class="t">windows.nodes</span>]]');
      L.push(kv('id', str(ids.get(node)), 9));
      if (node.t === 'leaf') {
        L.push(kv('kind', str('leaf'), 9));
        L.push(kv('pane', str(node.pane), 9));
      } else if (node.t === 'stack') {
        L.push(kv('kind', str('stack'), 9));
        L.push(kv('children', '[' + node.kids.map(function (k) { return str(ids.get(k)); }).join(', ') + ']', 9));
        L.push(kv('active', String(node.active), 9));
      } else {
        L.push(kv('kind', str('split'), 9));
        L.push(kv('direction', str(node.dir), 9));
        L.push(kv('children', '[' + node.kids.map(function (k) { return str(ids.get(k)); }).join(', ') + ']', 9));
        L.push(kv('ratios', '[' + node.ratios.map(function (r) { return +r.toFixed(3); }).join(', ') + ']', 9));
      }
    });
    leaves().forEach(function (id) {
      var p = panes[id];
      L.push('');
      L.push('[[<span class="t">windows.panes</span>]]');
      L.push(kv('id', str(id), 10));
      L.push(kv('kind', str(p.kind), 10));
      L.push(kv('title', str(p.title), 10));
      if (p.program) L.push(kv('program', str(p.program), 10));
      if (p.kind !== 'empty') {
        L.push(kv('cwd', str(p.shell === 'bash' ? '/home/you/src/winmux' : p.cwd), 10));
        if (p.kind === 'terminal') L.push(kv('cwd_source', str('shell-reported'), 10));
      }
      if (p.kind === 'foreign-app') L.push(kv('strategy', str('auto'), 10));
    });

    // Highlight lines that are new since the last write, the way a diff would.
    var before = {};
    lastToml.forEach(function (l) { before[l] = (before[l] || 0) + 1; });
    var first = lastToml.length === 0;
    var html = L.map(function (l) {
      if (!first && l && !before[l]) return '<span class="new">' + l + '</span>';
      if (before[l]) before[l]--;
      return l;
    }).join('\n');
    lastToml = L;
    tomlEl.innerHTML = '<code>' + html + '</code>';

    if (!first) {
      savedEl.textContent = 'saving…';
      savedEl.classList.add('is-dirty');
      clearTimeout(writeToml.t);
      writeToml.t = setTimeout(function () { savedEl.textContent = 'saved'; savedEl.classList.remove('is-dirty'); }, 500);
    }
  }

  // ---------- wiring ----------

  function say(text) { caption.textContent = text; }
  function changed(_, text) { render(); say(text); }

  tools.forEach(function (b) {
    b.addEventListener('click', function () {
      var act = b.getAttribute('data-act');
      if (act === 'split-columns') split('columns');
      else if (act === 'split-rows') split('rows');
      else if (act === 'new-tab') newTab();
      else if (act === 'close-pane') closePane();
      else if (act === 'freeze') freeze();
      else if (act === 'restart') restart();
    });
  });

  reset();
  render();
})();
