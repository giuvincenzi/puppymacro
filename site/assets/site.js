// Guide: the version select opens the same section in the chosen version.
const versionSelect = document.getElementById('version');
if (versionSelect) {
  versionSelect.addEventListener('change', () => {
    window.location.href = versionSelect.value;
  });
}

// Home pages: the language the visitor chose (from the menu or the banner), remembered in this browser only.
const languageKey = 'puppymacro.language';
function readLanguage() {
  try { return window.localStorage.getItem(languageKey); } catch { return null; }
}
function saveLanguage(code) {
  try { window.localStorage.setItem(languageKey, code); } catch { /* storage blocked: nothing is remembered */ }
}

// Language menu: a choice is remembered; a click outside or Esc closes it.
const languageMenu = document.querySelector('.language-menu');
if (languageMenu) {
  languageMenu.querySelectorAll('a[data-language]').forEach((link) => {
    link.addEventListener('click', () => saveLanguage(link.dataset.language));
  });
  document.addEventListener('click', (event) => {
    if (languageMenu.open && !languageMenu.contains(event.target)) languageMenu.open = false;
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && languageMenu.open) {
      languageMenu.open = false;
      languageMenu.querySelector('summary').focus();
    }
  });
}

// English home: when the browser prefers one of the other languages and the visitor has not chosen yet, a banner offers
// that language's home. The page never changes language by itself (search engines read the English one).
const offers = document.getElementById('language-offers');
const banner = document.getElementById('language-banner');
if (offers && banner && readLanguage() === null) {
  const pages = JSON.parse(offers.textContent);
  const offer = browserLanguage(Object.keys(pages));
  if (offer) {
    const page = pages[offer];
    banner.querySelector('.flag').replaceWith(flag(page));
    const text = banner.querySelector('.language-banner-text');
    text.textContent = page.text;
    text.lang = offer;
    const switchLink = banner.querySelector('a');
    switchLink.textContent = page.switch;
    switchLink.href = page.url;
    switchLink.hreflang = offer;
    switchLink.lang = offer;
    switchLink.addEventListener('click', () => saveLanguage(offer));
    const stay = banner.querySelector('button');
    stay.textContent = page.stay;
    stay.lang = offer;
    stay.addEventListener('click', () => {
      saveLanguage('en');
      banner.hidden = true;
    });
    banner.dir = page.dir;
    banner.hidden = false;
  }
}

// The first of the browser's languages that has a home page, or null when English comes first or none has one.
// Portuguese gives pt-BR; Chinese gives zh-Hans unless it is Traditional Chinese (as the app does).
function browserLanguage(codes) {
  const languages = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language || ''];
  for (const tag of languages) {
    const lower = tag.toLowerCase();
    const base = lower.split('-')[0];
    if (base === 'en') return null;
    if (base === 'zh') {
      if (/hant|-tw|-hk|-mo/.test(lower)) continue;
      if (codes.includes('zh-Hans')) return 'zh-Hans';
      continue;
    }
    if (base === 'pt' && codes.includes('pt-BR')) return 'pt-BR';
    const match = codes.find((code) => code.toLowerCase() === base);
    if (match) return match;
  }
  return null;
}

function flag(page) {
  if (page.flag) {
    const image = document.createElement('img');
    image.className = 'flag';
    image.src = page.flag;
    image.alt = '';
    return image;
  }
  const letter = document.createElement('span');
  letter.className = 'flag flag-letter';
  letter.setAttribute('aria-hidden', 'true');
  letter.textContent = page.letter;
  return letter;
}
