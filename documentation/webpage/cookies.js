/* ========================================
   SafeView.ai — Cookie Consent
   ======================================== */

(function () {
    'use strict';

    const STORAGE_KEY = 'safeview-cookies';

    function getConsent() {
        try {
            return JSON.parse(localStorage.getItem(STORAGE_KEY));
        } catch { return null; }
    }

    function saveConsent(prefs) {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs));
    }

    // Inject HTML
    function injectCookieUI() {
        // Banner
        const banner = document.createElement('div');
        banner.className = 'cookie-banner';
        banner.id = 'cookieBanner';
        banner.innerHTML = `
            <div class="cookie-banner-inner">
                <div class="cookie-banner-text">
                    <h4 data-i18n="cookie.banner.title">Korzystamy z plików cookie</h4>
                    <p data-i18n="cookie.banner.desc">Używamy plików cookie, aby zapewnić prawidłowe działanie strony i poprawić Twoje doświadczenia. Możesz dostosować preferencje w ustawieniach.</p>
                </div>
                <div class="cookie-banner-actions">
                    <button class="cookie-btn cookie-btn--primary" id="cookieAcceptAll" data-i18n="cookie.banner.accept">Zaakceptuj wszystkie</button>
                    <button class="cookie-btn cookie-btn--secondary" id="cookieOpenSettings" data-i18n="cookie.banner.settings">Ustawienia</button>
                </div>
            </div>
        `;

        // Modal overlay
        const overlay = document.createElement('div');
        overlay.className = 'cookie-overlay';
        overlay.id = 'cookieOverlay';
        overlay.innerHTML = `
            <div class="cookie-modal">
                <div class="cookie-modal-header">
                    <h3 data-i18n="cookie.modal.title">Ustawienia plików cookie</h3>
                    <button class="cookie-modal-close" id="cookieModalClose" aria-label="Close">
                        <svg width="14" height="14" viewBox="0 0 14 14" fill="none"><path d="M1 1l12 12M13 1L1 13" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>
                    </button>
                </div>
                <p class="cookie-modal-desc" data-i18n="cookie.modal.desc">Zarządzaj swoimi preferencjami dotyczącymi plików cookie. Możesz włączyć lub wyłączyć różne kategorie w dowolnym momencie.</p>
                <div class="cookie-categories">
                    <div class="cookie-category">
                        <div class="cookie-category-header">
                            <strong data-i18n="cookie.cat.necessary">Niezbędne</strong>
                            <label class="cookie-toggle">
                                <input type="checkbox" checked disabled>
                                <span class="cookie-toggle-slider"></span>
                            </label>
                        </div>
                        <p data-i18n="cookie.cat.necessary.desc">Te pliki cookie są niezbędne do prawidłowego funkcjonowania strony i nie można ich wyłączyć.</p>
                    </div>
                    <div class="cookie-category">
                        <div class="cookie-category-header">
                            <strong data-i18n="cookie.cat.analytics">Analityczne</strong>
                            <label class="cookie-toggle">
                                <input type="checkbox" id="cookieAnalytics" checked>
                                <span class="cookie-toggle-slider"></span>
                            </label>
                        </div>
                        <p data-i18n="cookie.cat.analytics.desc">Pomagają nam zrozumieć, jak odwiedzający korzystają ze strony, dzięki czemu możemy ją ulepszać.</p>
                    </div>
                    <div class="cookie-category">
                        <div class="cookie-category-header">
                            <strong data-i18n="cookie.cat.marketing">Marketingowe</strong>
                            <label class="cookie-toggle">
                                <input type="checkbox" id="cookieMarketing" checked>
                                <span class="cookie-toggle-slider"></span>
                            </label>
                        </div>
                        <p data-i18n="cookie.cat.marketing.desc">Używane do wyświetlania spersonalizowanych reklam i śledzenia skuteczności kampanii reklamowych.</p>
                    </div>
                    <div class="cookie-category">
                        <div class="cookie-category-header">
                            <strong data-i18n="cookie.cat.functional">Funkcjonalne</strong>
                            <label class="cookie-toggle">
                                <input type="checkbox" id="cookieFunctional" checked>
                                <span class="cookie-toggle-slider"></span>
                            </label>
                        </div>
                        <p data-i18n="cookie.cat.functional.desc">Pozwalają na zapamiętywanie preferencji użytkownika i personalizację doświadczeń.</p>
                    </div>
                </div>
                <div class="cookie-modal-footer">
                    <button class="cookie-btn cookie-btn--secondary" id="cookieCancel" data-i18n="cookie.modal.cancel">Anuluj</button>
                    <button class="cookie-btn cookie-btn--save" id="cookieSave" data-i18n="cookie.modal.save">Zapisz ustawienia</button>
                </div>
            </div>
        `;

        document.body.appendChild(banner);
        document.body.appendChild(overlay);
    }

    function init() {
        injectCookieUI();

        const banner = document.getElementById('cookieBanner');
        const overlay = document.getElementById('cookieOverlay');
        const btnAcceptAll = document.getElementById('cookieAcceptAll');
        const btnSettings = document.getElementById('cookieOpenSettings');
        const btnClose = document.getElementById('cookieModalClose');
        const btnCancel = document.getElementById('cookieCancel');
        const btnSave = document.getElementById('cookieSave');
        const chkAnalytics = document.getElementById('cookieAnalytics');
        const chkMarketing = document.getElementById('cookieMarketing');
        const chkFunctional = document.getElementById('cookieFunctional');

        // If already consented, don't show
        const existing = getConsent();
        if (existing) return;

        // Apply i18n translations to injected elements
        if (typeof i18n !== 'undefined' && i18n.apply) {
            i18n.apply();
        }

        // Show banner after small delay
        setTimeout(() => banner.classList.add('visible'), 500);

        function hideBanner() {
            banner.classList.remove('visible');
        }

        function showModal() {
            overlay.classList.add('visible');
            document.body.style.overflow = 'hidden';
        }

        function hideModal() {
            overlay.classList.remove('visible');
            document.body.style.overflow = '';
        }

        btnAcceptAll.addEventListener('click', () => {
            saveConsent({ necessary: true, analytics: true, marketing: true, functional: true });
            hideBanner();
        });

        btnSettings.addEventListener('click', () => {
            hideBanner();
            showModal();
        });

        btnClose.addEventListener('click', () => {
            hideModal();
            setTimeout(() => banner.classList.add('visible'), 100);
        });

        btnCancel.addEventListener('click', () => {
            hideModal();
            // Re-show banner
            setTimeout(() => banner.classList.add('visible'), 100);
        });

        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) {
                hideModal();
                setTimeout(() => banner.classList.add('visible'), 100);
            }
        });

        btnSave.addEventListener('click', () => {
            saveConsent({
                necessary: true,
                analytics: chkAnalytics.checked,
                marketing: chkMarketing.checked,
                functional: chkFunctional.checked
            });
            hideModal();
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
