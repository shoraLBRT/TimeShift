// Classic (non-module) script for <head>: applies the saved theme before the first paint, so there is no flash.
// <script src="/core/boot.js"></script>
(function () {
  var pref = 'auto';
  try { pref = localStorage.getItem('ts.theme') || 'auto'; } catch (e) { /* private mode */ }
  var dark = pref === 'dark' || (pref !== 'light' && window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches);
  var root = document.documentElement;
  root.setAttribute('data-theme', dark ? 'dark' : 'light');
  root.setAttribute('data-theme-pref', pref === 'light' || pref === 'dark' ? pref : 'auto');
  root.style.colorScheme = dark ? 'dark' : 'light';
})();
