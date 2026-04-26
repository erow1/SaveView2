/* ========================================
   SafeView.ai — Main JavaScript
   ======================================== */

// ---- Reveal background video only after it starts playing (hide poster flash) ----
(function() {
    document.querySelectorAll('.video-bg video').forEach(function(v) {
        v.addEventListener('playing', function() { v.classList.add('video-ready'); }, { once: true });
    });
})();

document.addEventListener('DOMContentLoaded', () => {

    // ---- Eye Blink on Page Load & Click ----
    const eyeParts = document.querySelectorAll('.eye-lid, .eye-pupil');

    function blinkEyes() {
        eyeParts.forEach(el => {
            el.classList.remove('blink');
            void el.offsetWidth;
            el.classList.add('blink');
        });
    }

    eyeParts.forEach(el => {
        el.addEventListener('animationend', () => el.classList.remove('blink'));
    });

    // Blink on page load (when navigating between pages)
    setTimeout(blinkEyes, 200);

    // Blink on click (skip if clicking a link — page load blink will handle it)
    document.addEventListener('click', (e) => {
        if (!e.target.closest('a')) blinkEyes();
    });

    // ---- Navbar Scroll Effect ----
    const navbar = document.getElementById('navbar');
    const handleScroll = () => {
        if (window.scrollY > 50) {
            navbar.classList.add('scrolled');
        } else {
            navbar.classList.remove('scrolled');
        }
    };
    window.addEventListener('scroll', handleScroll, { passive: true });

    // ---- Mobile Nav Toggle ----
    const navToggle = document.getElementById('navToggle');
    const navLinks = document.getElementById('navLinks');
    const navCenter = document.querySelector('.nav-center');

    navToggle.addEventListener('click', () => {
        navToggle.classList.toggle('active');
        navLinks.classList.toggle('active');
        if (navCenter) navCenter.classList.toggle('active');
        navbar.classList.toggle('menu-open', navLinks.classList.contains('active'));
        document.body.style.overflow = navLinks.classList.contains('active') ? 'hidden' : '';
    });

    // Close mobile nav on link click
    navLinks.querySelectorAll('a').forEach(link => {
        link.addEventListener('click', () => {
            navToggle.classList.remove('active');
            navLinks.classList.remove('active');
            if (navCenter) navCenter.classList.remove('active');
            navbar.classList.remove('menu-open');
            document.body.style.overflow = '';
        });
    });

    // ---- Smooth Scroll for anchor links ----
    document.querySelectorAll('a[href^="#"]').forEach(anchor => {
        anchor.addEventListener('click', (e) => {
            const target = document.querySelector(anchor.getAttribute('href'));
            if (target) {
                e.preventDefault();
                const offset = 80;
                const y = target.getBoundingClientRect().top + window.pageYOffset - offset;
                window.scrollTo({ top: y, behavior: 'smooth' });
            }
        });
    });

    // ---- Scroll Animations (Intersection Observer) ----
    const animatedElements = document.querySelectorAll('[data-animate]');
    const observerOptions = {
        root: null,
        rootMargin: '0px 0px -60px 0px',
        threshold: 0.1
    };

    const observer = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                entry.target.classList.add('visible');
                observer.unobserve(entry.target);
            }
        });
    }, observerOptions);

    animatedElements.forEach(el => observer.observe(el));

    // ---- Stat Bar Animation ----
    const statBars = document.querySelectorAll('.stat-bar-fill');
    const statObserver = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                const bar = entry.target;
                const width = bar.style.width;
                bar.style.width = '0';
                requestAnimationFrame(() => {
                    requestAnimationFrame(() => {
                        bar.style.width = width;
                    });
                });
                statObserver.unobserve(bar);
            }
        });
    }, { threshold: 0.3 });

    statBars.forEach(bar => statObserver.observe(bar));

    // ---- Counter Animation ----
    const counters = document.querySelectorAll('[data-count]');
    const counterObserver = new IntersectionObserver((entries) => {
        entries.forEach(entry => {
            if (entry.isIntersecting) {
                const el = entry.target;
                const target = parseFloat(el.dataset.count);
                const prefix = el.dataset.prefix || '';
                const duration = 3000;
                const startTime = performance.now();
                const isDecimal = target % 1 !== 0;

                const animate = (currentTime) => {
                    const elapsed = currentTime - startTime;
                    const progress = Math.min(elapsed / duration, 1);
                    // Ease out cubic
                    const eased = 1 - Math.pow(1 - progress, 3);
                    const current = eased * target;

                    if (isDecimal) {
                        el.textContent = prefix + current.toFixed(1);
                    } else {
                        el.textContent = prefix + Math.round(current);
                    }

                    if (progress < 1) {
                        requestAnimationFrame(animate);
                    }
                };

                requestAnimationFrame(animate);
                counterObserver.unobserve(el);
            }
        });
    }, { threshold: 0.5 });

    counters.forEach(counter => counterObserver.observe(counter));

    // ---- Contact Form (Web3Forms) ----
    const contactForm = document.getElementById('contactForm');
    if (contactForm) {
        contactForm.addEventListener('submit', async (e) => {
            e.preventDefault();

            const btn = contactForm.querySelector('button[type="submit"]');
            const originalText = btn.innerHTML;

            btn.innerHTML = `
                <svg width="20" height="20" viewBox="0 0 20 20" fill="none" class="spin"><circle cx="10" cy="10" r="8" stroke="currentColor" stroke-width="2" stroke-dasharray="40" stroke-dashoffset="10" stroke-linecap="round"/></svg>
                Wysyłanie...
            `;
            btn.disabled = true;

            try {
                const formData = new FormData(contactForm);
                const res = await fetch('https://api.web3forms.com/submit', {
                    method: 'POST',
                    body: formData
                });
                const data = await res.json();

                if (data.success) {
                    btn.innerHTML = `
                        <svg width="20" height="20" viewBox="0 0 20 20" fill="none"><path d="M5 10l3 3 7-7" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/></svg>
                        Wysłano!
                    `;
                    btn.style.background = 'var(--yellow)';
                    btn.style.color = 'var(--dark)';
                    setTimeout(() => {
                        window.location.href = 'thank-you.html';
                    }, 800);
                } else {
                    throw new Error(data.message || 'Błąd wysyłania');
                }
            } catch (err) {
                btn.innerHTML = originalText;
                btn.disabled = false;
                alert('Wystąpił błąd przy wysyłaniu. Spróbuj ponownie lub napisz na office@safeview.ai');
                console.error('Form error:', err);
            }
        });
    }

    // ---- Dynamic Year in Footer ----
    const yearEl = document.querySelector('.footer-bottom p');
    if (yearEl) {
        const year = new Date().getFullYear();
        yearEl.innerHTML = yearEl.innerHTML.replace(/\d{4}/, year);
    }

    // ---- FAQ Accordion ----
    const faqItems = document.querySelectorAll('.faq-item');
    faqItems.forEach(item => {
        const question = item.querySelector('.faq-question');
        if (question) {
            question.addEventListener('click', () => {
                const isActive = item.classList.contains('active');
                // Close all
                faqItems.forEach(i => i.classList.remove('active'));
                // Open clicked (if wasn't open)
                if (!isActive) {
                    item.classList.add('active');
                }
            });
        }
    });

    // ---- Add spin animation for form loading ----
    const style = document.createElement('style');
    style.textContent = `
        @keyframes spin {
            to { transform: rotate(360deg); }
        }
        .spin {
            animation: spin 1s linear infinite;
        }
    `;
    document.head.appendChild(style);

    // ---- Steps progressive fill — auto-animate when section visible ----
    const stepsContainer = document.querySelector('.steps');
    if (stepsContainer) {
        const steps = Array.from(stepsContainer.querySelectorAll('.step'));
        let stepsAnimated = false;

        // Calculate exact center of step-number circles
        const firstNumber = stepsContainer.querySelector('.step-number');
        const lineLeft = firstNumber
            ? firstNumber.offsetLeft + firstNumber.offsetWidth / 2 - 1
            : 27;

        // Sync CSS ::before line position
        stepsContainer.style.setProperty('--line-left', lineLeft + 'px');

        // Create fill line element
        const fillLine = document.createElement('div');
        fillLine.className = 'steps-fill-line';
        fillLine.style.cssText = 'position:absolute;top:0;width:2px;background:#FFD600;z-index:1;pointer-events:none;height:0;transition:none;left:' + lineLeft + 'px;';
        stepsContainer.appendChild(fillLine);

        function animateSteps() {
            if (stepsAnimated) return;
            stepsAnimated = true;

            const containerHeight = stepsContainer.offsetHeight;
            const duration = 4423; // total animation time in ms
            const startTime = performance.now();

            // Pre-calculate step trigger points (relative to container top)
            const stepTriggers = steps.map(step => {
                const num = step.querySelector('.step-number');
                return num
                    ? step.offsetTop + num.offsetTop + num.offsetHeight / 2
                    : step.offsetTop + 28;
            });

            function tick(now) {
                const elapsed = now - startTime;
                const progress = Math.min(elapsed / duration, 1);
                // Ease out cubic
                const eased = 1 - Math.pow(1 - progress, 3);
                const fillHeight = eased * containerHeight;

                fillLine.style.height = fillHeight + 'px';

                // Fill circles progressively
                stepTriggers.forEach((trigger, i) => {
                    if (fillHeight >= trigger) {
                        steps[i].classList.add('step--filled');
                    }
                });

                if (progress < 1) {
                    requestAnimationFrame(tick);
                }
            }

            requestAnimationFrame(tick);
        }

        // Trigger when section enters viewport
        const stepsObserver = new IntersectionObserver((entries) => {
            entries.forEach(entry => {
                if (entry.isIntersecting) {
                    animateSteps();
                    stepsObserver.disconnect();
                }
            });
        }, { threshold: 0.5 });

        stepsObserver.observe(stepsContainer);
    }

});
