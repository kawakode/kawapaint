const header = document.querySelector('[data-header]');
const nav = document.querySelector('[data-nav]');
const navToggle = document.querySelector('[data-nav-toggle]');

const updateHeader = () => header?.classList.toggle('is-scrolled', window.scrollY > 12);
updateHeader();
window.addEventListener('scroll', updateHeader, { passive: true });

navToggle?.addEventListener('click', () => {
  const open = navToggle.getAttribute('aria-expanded') !== 'true';
  navToggle.setAttribute('aria-expanded', String(open));
  nav?.classList.toggle('is-open', open);
});

nav?.querySelectorAll('a').forEach(link => link.addEventListener('click', () => {
  navToggle?.setAttribute('aria-expanded', 'false');
  nav.classList.remove('is-open');
}));

const revealItems = document.querySelectorAll('.reveal');
if ('IntersectionObserver' in window) {
  const revealObserver = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (entry.isIntersecting) {
        entry.target.classList.add('is-visible');
        revealObserver.unobserve(entry.target);
      }
    });
  }, { threshold: 0.08, rootMargin: '0px 0px -24px' });
  revealItems.forEach(item => revealObserver.observe(item));
} else {
  revealItems.forEach(item => item.classList.add('is-visible'));
}

const dialog = document.querySelector('[data-lightbox-dialog]');
const dialogImage = document.querySelector('[data-lightbox-image]');
document.querySelectorAll('[data-lightbox]').forEach(button => {
  button.addEventListener('click', () => {
    if (!dialog || !dialogImage) return;
    dialogImage.src = button.dataset.lightbox;
    const sourceImage = button.querySelector('img');
    dialogImage.alt = sourceImage?.alt || 'Expanded KawaPaint screenshot';
    dialog.showModal();
  });
});
document.querySelector('[data-lightbox-close]')?.addEventListener('click', () => dialog?.close());
dialog?.addEventListener('click', event => {
  if (event.target === dialog) dialog.close();
});

const formatBytes = bytes => {
  const megabytes = bytes / 1024 / 1024;
  return `${megabytes.toFixed(1)} MB`;
};

const refreshLatestRelease = async () => {
  try {
    const response = await fetch('https://api.github.com/repos/kawakode/kawapaint/releases/latest', {
      headers: { Accept: 'application/vnd.github+json' }
    });
    if (!response.ok) return;

    const release = await response.json();
    document.querySelectorAll('[data-release-label]').forEach(label => {
      label.textContent = release.name || release.tag_name || 'Latest release';
    });
    document.querySelectorAll('[data-release-page]').forEach(link => {
      link.href = release.html_url;
    });

    const platforms = {
      windows: asset => /win-x64\.zip$/i.test(asset.name),
      linux: asset => /linux-x64\.zip$/i.test(asset.name)
    };

    Object.entries(platforms).forEach(([platform, matches]) => {
      const asset = release.assets?.find(matches);
      if (!asset) return;
      const link = document.querySelector(`[data-download="${platform}"]`);
      const size = document.querySelector(`[data-size="${platform}"]`);
      if (link) link.href = asset.browser_download_url;
      if (size) size.textContent = formatBytes(asset.size);
    });
  } catch {
    // Static links remain valid if GitHub's API is unavailable.
  }
};

refreshLatestRelease();
document.querySelector('[data-year]').textContent = String(new Date().getFullYear());
