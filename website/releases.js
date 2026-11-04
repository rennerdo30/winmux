// WinMux homepage: fills in the newest stable release and its download links from GitHub.
// Without this script (or without network) every link goes to the GitHub releases page.
(function () {
  'use strict';

  // /releases/latest is the newest non-prerelease, non-draft release: the same one
  // About WinMux offers on the default "Stable only" channel.
  var API = 'https://api.github.com/repos/rennerdo30/winmux/releases/latest';
  var DOWNLOADS = 'https://github.com/rennerdo30/winmux/releases/download/';

  // Only accept links that point at this repository's release downloads.
  // Assets are WinMux-<version>-win-x64-setup.exe, WinMux-<version>-win-x64.zip and checksums.txt.
  function asset(r, suffix) {
    var list = r.assets || [];
    for (var i = 0; i < list.length; i++) {
      var a = list[i];
      var name = a.name || '';
      var match = suffix === 'checksums.txt'
        ? name === 'checksums.txt'
        : /^WinMux-/.test(name) && name.slice(-suffix.length - 1) === '-' + suffix;
      if (match && String(a.browser_download_url).indexOf(DOWNLOADS) === 0) return a.browser_download_url;
    }
    return null;
  }

  function fill(r) {
    var ok = r && /^v?\d+\.\d+\.\d+$/.test(r.tag_name || '');
    document.querySelectorAll('[data-release="latest"]').forEach(function (el) {
      var field = el.getAttribute('data-field');
      if (field === 'missing') el.hidden = !!ok;
      if (field === 'found') el.hidden = !ok;
      if (!ok) return;
      var version = r.tag_name.replace(/^v/, '');
      if (field === 'version') el.textContent = version;
      if (field === 'date') el.textContent = new Date(r.published_at).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
      if (field === 'hero-label') el.textContent = 'Download WinMux ' + version;
    });
    if (!ok) return;
    document.querySelectorAll('[data-asset]').forEach(function (el) {
      var url = asset(r, el.getAttribute('data-asset'));
      el.href = url || r.html_url;
    });
  }

  if (!window.fetch) return;
  fetch(API, { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (res) { return res.ok ? res.json() : null; })
    .then(fill)
    .catch(function () { /* keep the static links */ });
})();
