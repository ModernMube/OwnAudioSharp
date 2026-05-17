/* ─── Theme ───────────────────────────────────────────────────────────────── */
(function () {
  const saved = localStorage.getItem('theme') ||
    (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
  document.documentElement.setAttribute('data-theme', saved);
})();

document.addEventListener('DOMContentLoaded', () => {

  /* Theme toggle */
  const toggleBtn = document.getElementById('theme-toggle');
  const updateIcon = () => {
    const isDark = document.documentElement.getAttribute('data-theme') !== 'light';
    if (toggleBtn) toggleBtn.innerHTML = isDark ? sunIcon() : moonIcon();
  };
  if (toggleBtn) {
    updateIcon();
    toggleBtn.addEventListener('click', () => {
      const next = document.documentElement.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
      document.documentElement.setAttribute('data-theme', next);
      localStorage.setItem('theme', next);
      updateIcon();
    });
  }

  /* Mobile sidebar */
  const sidebar        = document.getElementById('sidebar');
  const overlay        = document.getElementById('sidebar-overlay');
  const sidebarToggle  = document.getElementById('sidebar-toggle');

  function openSidebar() {
    if (sidebar)  sidebar.classList.add('open');
    if (overlay)  overlay.classList.add('show');
  }
  function closeSidebar() {
    if (sidebar)  sidebar.classList.remove('open');
    if (overlay)  overlay.classList.remove('show');
  }
  if (sidebarToggle) sidebarToggle.addEventListener('click', openSidebar);
  if (overlay)       overlay.addEventListener('click', closeSidebar);

  /* Active sidebar link — page level */
  const currentPage = location.pathname.split('/').pop() || 'index.html';
  document.querySelectorAll('.sidebar-nav a').forEach(a => {
    const href = a.getAttribute('href') || '';
    const page = href.split('#')[0];
    if (page && (page === currentPage || page.endsWith('/' + currentPage))) {
      a.classList.add('active');
    }
  });

  /* Scroll-spy — highlight sub-link for current section */
  const headings = Array.from(document.querySelectorAll('main h2[id], main h3[id]'));
  if (headings.length > 0) {
    const subLinks = {};
    document.querySelectorAll('.sidebar-nav a.sub').forEach(a => {
      const href = a.getAttribute('href') || '';
      const [page, hash] = href.split('#');
      if (hash && (!page || page === currentPage || page.endsWith('/' + currentPage))) {
        subLinks[hash] = a;
      }
    });

    let lastActiveId = null;

    function updateScrollSpy() {
      const offset = 100; // px below viewport top (accounts for sticky nav)
      const scrollY = window.scrollY + offset;
      let current = null;
      for (const h of headings) {
        if (h.offsetTop <= scrollY) current = h;
      }
      const id = current ? current.id : null;
      if (id === lastActiveId) return;
      lastActiveId = id;
      document.querySelectorAll('.sidebar-nav a.sub').forEach(a => a.classList.remove('active'));
      if (id && subLinks[id]) subLinks[id].classList.add('active');
    }

    window.addEventListener('scroll', updateScrollSpy, { passive: true });
    updateScrollSpy();
  }

  /* Copy buttons */
  document.querySelectorAll('.code-block').forEach(block => {
    const btn = block.querySelector('.copy-btn');
    const pre = block.querySelector('pre');
    if (!btn || !pre) return;
    btn.addEventListener('click', () => {
      navigator.clipboard.writeText(pre.textContent).then(() => {
        btn.textContent = 'Copied!';
        btn.classList.add('copied');
        setTimeout(() => { btn.textContent = 'Copy'; btn.classList.remove('copied'); }, 2000);
      });
    });
  });

  /* Anchor links */
  document.querySelectorAll('h2[id], h3[id]').forEach(h => {
    const a = document.createElement('a');
    a.href = '#' + h.id;
    a.className = 'anchor-link';
    a.textContent = '#';
    h.appendChild(a);
  });

});

function sunIcon() {
  return `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>`;
}
function moonIcon() {
  return `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>`;
}
