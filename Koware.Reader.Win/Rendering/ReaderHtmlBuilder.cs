// Author: Ilgaz Mehmetoğlu
// Builds the embedded HTML/JS page used inside the WebView manga reader.
using System.Net;
using System.Text.Json;
using Koware.Reader.Win.Startup;

namespace Koware.Reader.Win.Rendering;

internal static class ReaderHtmlBuilder
{
    public static string Build(ReaderArguments args)
    {
        var pagesJson = JsonSerializer.Serialize(args.Pages);
        var titleJson = JsonSerializer.Serialize(args.Title);
        var encodedTitle = WebUtility.HtmlEncode(args.Title);

        const string template = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
    <title>{{TITLE}}</title>
    <style>
        :root {
            color-scheme: dark;
            --bg: #0f172a;
            --panel: #0f172a;
            --border: rgba(226, 232, 240, 0.1);
            --text: #e2e8f0;
            --muted: #94a3b8;
            --accent: #38bdf8;
            --error: #f97066;
        }

        * { box-sizing: border-box; margin: 0; padding: 0; }

        html, body {
            height: 100%;
            overflow: hidden;
        }

        body {
            background: var(--bg);
            color: var(--text);
            font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
            display: flex;
            flex-direction: column;
        }

        #header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 10px 16px;
            background: linear-gradient(145deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.01)) var(--panel);
            border-bottom: 1px solid var(--border);
            gap: 12px;
            flex-shrink: 0;
        }

        #title {
            font-weight: 700;
            font-size: 14px;
            letter-spacing: 0.01em;
            color: var(--text);
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
            flex: 1;
            min-width: 0;
        }

        .controls {
            display: flex;
            align-items: center;
            gap: 6px;
            flex-shrink: 0;
        }

        .btn {
            border: 1px solid rgba(226, 232, 240, 0.15);
            background: rgba(226, 232, 240, 0.08);
            color: var(--text);
            border-radius: 8px;
            padding: 6px 12px;
            font-weight: 600;
            font-size: 12px;
            cursor: pointer;
            transition: background 0.2s ease, border-color 0.2s ease;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 4px;
        }

        .btn:hover:not(:disabled) {
            background: rgba(226, 232, 240, 0.14);
            border-color: rgba(226, 232, 240, 0.25);
        }

        .btn:disabled {
            opacity: 0.4;
            cursor: not-allowed;
        }

        .btn.active {
            background: rgba(56, 189, 248, 0.2);
            border-color: rgba(56, 189, 248, 0.4);
            color: var(--accent);
        }

        .btn-sm {
            padding: 5px 10px;
            font-size: 11px;
        }

        #page-info {
            font-size: 12px;
            color: var(--muted);
            font-weight: 600;
            min-width: 70px;
            text-align: center;
        }

        #reader {
            flex: 1;
            overflow: auto;
            display: flex;
            justify-content: center;
            align-items: flex-start;
            padding: 16px;
            scroll-behavior: smooth;
        }

        #reader.fit-width {
            align-items: flex-start;
        }

        #reader.fit-height {
            align-items: center;
        }

        #reader.fit-original {
            align-items: flex-start;
        }

        #pages-container {
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 8px;
        }

        /* Double page mode */
        #reader.double-page #pages-container {
            flex-direction: row;
            flex-wrap: wrap;
            justify-content: center;
            gap: 4px;
        }

        #reader.double-page .page-wrapper {
            max-width: calc(50% - 4px);
        }

        #reader.double-page.rtl #pages-container {
            flex-direction: row-reverse;
        }

        /* RTL mode */
        #reader.rtl #pages-container {
            direction: rtl;
        }

        .page-wrapper {
            position: relative;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 150px;
            background: rgba(255, 255, 255, 0.02);
            border-radius: 6px;
            border: 1px solid var(--border);
            overflow: hidden;
        }

        .page-wrapper.loading::after {
            content: "";
            position: absolute;
            width: 28px;
            height: 28px;
            border: 3px solid var(--border);
            border-top-color: var(--accent);
            border-radius: 50%;
            animation: spin 0.8s linear infinite;
        }

        @keyframes spin {
            to { transform: rotate(360deg); }
        }

        .page-img {
            display: block;
            max-width: 100%;
            height: auto;
            transition: opacity 0.2s ease;
        }

        .page-img.loading {
            opacity: 0;
        }

        .page-wrapper.error {
            background: rgba(249, 112, 102, 0.1);
            border-color: rgba(249, 112, 102, 0.3);
            min-height: 100px;
            padding: 16px;
        }

        .page-wrapper.error::after {
            content: "Failed to load";
            color: var(--error);
            font-size: 12px;
            font-weight: 600;
        }

        /* Fit modes */
        .fit-width .page-img {
            width: 100%;
            max-width: min(100%, 900px);
            height: auto;
        }

        .fit-height .page-wrapper {
            height: calc(100vh - 110px);
        }

        .fit-height .page-img {
            width: auto;
            max-height: calc(100vh - 130px);
            max-width: 100%;
        }

        .fit-original .page-img {
            width: auto;
            max-width: none;
        }

        /* Single page mode */
        #reader.single-page #pages-container {
            gap: 0;
        }

        #reader.single-page .page-wrapper {
            display: none;
        }

        #reader.single-page .page-wrapper.active {
            display: flex;
        }

        /* Zoom */
        #reader[data-zoom="125"] .page-img { transform: scale(1.25); transform-origin: top center; }
        #reader[data-zoom="150"] .page-img { transform: scale(1.5); transform-origin: top center; }
        #reader[data-zoom="175"] .page-img { transform: scale(1.75); transform-origin: top center; }
        #reader[data-zoom="200"] .page-img { transform: scale(2); transform-origin: top center; }

        #footer {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 8px 16px;
            background: linear-gradient(145deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.01)) var(--panel);
            border-top: 1px solid var(--border);
            gap: 12px;
            flex-shrink: 0;
        }

        .nav-controls {
            display: flex;
            align-items: center;
            gap: 8px;
        }

        .nav-btn {
            padding: 8px 16px;
        }

        /* Page slider */
        #page-slider-container {
            flex: 1;
            display: flex;
            align-items: center;
            gap: 10px;
            max-width: 400px;
        }

        #page-slider {
            flex: 1;
            height: 6px;
            -webkit-appearance: none;
            background: rgba(255, 255, 255, 0.15);
            border-radius: 3px;
            cursor: pointer;
        }

        #page-slider::-webkit-slider-thumb {
            -webkit-appearance: none;
            width: 16px;
            height: 16px;
            background: var(--accent);
            border-radius: 50%;
            cursor: pointer;
            box-shadow: 0 2px 6px rgba(0, 0, 0, 0.3);
        }

        #page-slider.rtl {
            direction: rtl;
        }

        kbd {
            display: inline-block;
            padding: 2px 5px;
            font-size: 10px;
            font-family: ui-monospace, monospace;
            background: rgba(255, 255, 255, 0.08);
            border: 1px solid var(--border);
            border-radius: 3px;
            color: var(--muted);
            margin-left: 3px;
        }

        /* Dropdown */
        .dropdown {
            position: relative;
        }

        .dropdown-menu {
            position: absolute;
            top: 100%;
            right: 0;
            margin-top: 4px;
            background: rgba(15, 23, 42, 0.98);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 4px;
            min-width: 120px;
            display: none;
            z-index: 10;
            box-shadow: 0 12px 32px rgba(0, 0, 0, 0.4);
        }

        .dropdown.open .dropdown-menu {
            display: block;
        }

        .dropdown-item {
            display: block;
            width: 100%;
            padding: 6px 10px;
            background: transparent;
            border: none;
            color: var(--text);
            font-size: 12px;
            text-align: left;
            cursor: pointer;
            border-radius: 5px;
            transition: background 0.15s ease;
        }

        .dropdown-item:hover {
            background: rgba(255, 255, 255, 0.08);
        }

        .dropdown-item.active {
            background: rgba(56, 189, 248, 0.15);
            color: var(--accent);
        }

        /* Progress bar */
        #progress-bar {
            position: fixed;
            top: 0;
            left: 0;
            height: 3px;
            background: var(--accent);
            transition: width 0.2s ease;
            z-index: 100;
        }

        /* Page indicator toast */
        #page-toast {
            position: fixed;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            background: rgba(0, 0, 0, 0.8);
            color: var(--text);
            padding: 12px 24px;
            border-radius: 10px;
            font-weight: 600;
            font-size: 18px;
            pointer-events: none;
            opacity: 0;
            transition: opacity 0.2s ease;
            z-index: 50;
        }

        #page-toast.visible {
            opacity: 1;
        }
    </style>
</head>
<body>
    <div id="progress-bar" style="width: 0%"></div>
    <div id="page-toast"></div>
    <header id="header">
        <div id="title">{{TITLE}}</div>
        <div class="controls">
            <button class="btn btn-sm" id="rtl-btn" type="button" title="Right-to-Left reading">RTL</button>
            <button class="btn btn-sm" id="double-btn" type="button" title="Double page spread">2-Page</button>
            <div class="dropdown" id="fit-dropdown">
                <button class="btn btn-sm" id="fit-btn" type="button">Fit Width</button>
                <div class="dropdown-menu">
                    <button class="dropdown-item active" data-fit="width">Fit Width</button>
                    <button class="dropdown-item" data-fit="height">Fit Height</button>
                    <button class="dropdown-item" data-fit="original">Original Size</button>
                </div>
            </div>
            <div class="dropdown" id="zoom-dropdown">
                <button class="btn btn-sm" id="zoom-btn" type="button">100%</button>
                <div class="dropdown-menu">
                    <button class="dropdown-item active" data-zoom="100">100%</button>
                    <button class="dropdown-item" data-zoom="125">125%</button>
                    <button class="dropdown-item" data-zoom="150">150%</button>
                    <button class="dropdown-item" data-zoom="175">175%</button>
                    <button class="dropdown-item" data-zoom="200">200%</button>
                </div>
            </div>
            <button class="btn btn-sm" id="mode-btn" type="button">Scroll</button>
        </div>
    </header>
    <main id="reader" class="fit-width" data-zoom="100">
        <div id="pages-container"></div>
    </main>
    <footer id="footer">
        <div class="nav-controls">
            <button class="btn nav-btn" id="prev-btn" type="button" disabled>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="15 18 9 12 15 6"></polyline></svg>
                <kbd>←</kbd>
            </button>
        </div>
        <div id="page-slider-container">
            <span id="page-info">1 / 1</span>
            <input type="range" id="page-slider" min="1" max="1" value="1" />
        </div>
        <div class="nav-controls">
            <button class="btn nav-btn" id="next-btn" type="button">
                <kbd>→</kbd>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="9 18 15 12 9 6"></polyline></svg>
            </button>
        </div>
    </footer>
    <script>
        const pages = {{PAGES_JSON}};
        const title = {{TITLE_JSON}};
        const reader = document.getElementById("reader");
        const container = document.getElementById("pages-container");
        const pageInfo = document.getElementById("page-info");
        const prevBtn = document.getElementById("prev-btn");
        const nextBtn = document.getElementById("next-btn");
        const modeBtn = document.getElementById("mode-btn");
        const fitBtn = document.getElementById("fit-btn");
        const fitDropdown = document.getElementById("fit-dropdown");
        const zoomBtn = document.getElementById("zoom-btn");
        const zoomDropdown = document.getElementById("zoom-dropdown");
        const progressBar = document.getElementById("progress-bar");
        const pageSlider = document.getElementById("page-slider");
        const pageToast = document.getElementById("page-toast");
        const rtlBtn = document.getElementById("rtl-btn");
        const doubleBtn = document.getElementById("double-btn");

        let currentPage = 0;
        let singlePageMode = false;
        let rtlMode = false;
        let doublePageMode = false;
        let currentFit = "width";
        let currentZoom = 100;
        let loadedCount = 0;
        let toastTimeout = null;

        function init() {
            pageSlider.max = pages.length;
            renderPages();
            updatePageInfo();
            updateNavButtons();
            wireEvents();
            loadPrefs();
            restorePosition();
        }

        function renderPages() {
            container.innerHTML = "";
            pages.forEach((page, idx) => {
                const wrapper = document.createElement("div");
                wrapper.className = "page-wrapper loading";
                wrapper.dataset.page = idx;

                const img = document.createElement("img");
                img.className = "page-img loading";
                img.alt = `Page ${page.pageNumber || idx + 1}`;
                // Preload first 5 pages eagerly
                img.loading = idx < 5 ? "eager" : "lazy";
                
                img.onload = () => {
                    wrapper.classList.remove("loading");
                    img.classList.remove("loading");
                    loadedCount++;
                    updateProgress();
                    // Preload next pages when current loads
                    preloadAhead(idx);
                };
                
                img.onerror = () => {
                    wrapper.classList.remove("loading");
                    wrapper.classList.add("error");
                    img.style.display = "none";
                    loadedCount++;
                    updateProgress();
                };

                img.src = page.url || page.Url;
                wrapper.appendChild(img);
                container.appendChild(wrapper);
            });

            if (pages.length > 0) {
                container.children[0].classList.add("active");
            }
        }

        function preloadAhead(fromIdx) {
            // Preload next 3 pages
            for (let i = 1; i <= 3; i++) {
                const nextIdx = fromIdx + i;
                if (nextIdx < pages.length) {
                    const wrapper = container.children[nextIdx];
                    const img = wrapper?.querySelector("img");
                    if (img && !img.src && (pages[nextIdx].url || pages[nextIdx].Url)) {
                        img.src = pages[nextIdx].url || pages[nextIdx].Url;
                    }
                }
            }
        }

        function updateProgress() {
            const pct = pages.length > 0 ? (loadedCount / pages.length) * 100 : 0;
            progressBar.style.width = `${pct}%`;
            if (pct >= 100) {
                setTimeout(() => { progressBar.style.opacity = "0"; }, 500);
            }
        }

        function showToast(text) {
            pageToast.textContent = text;
            pageToast.classList.add("visible");
            clearTimeout(toastTimeout);
            toastTimeout = setTimeout(() => pageToast.classList.remove("visible"), 800);
        }

        function updatePageInfo() {
            const total = pages.length;
            const current = currentPage + 1;
            pageInfo.textContent = `${current} / ${total}`;
            pageSlider.value = current;
        }

        function updateNavButtons() {
            if (rtlMode) {
                prevBtn.disabled = currentPage >= pages.length - 1;
                nextBtn.disabled = currentPage === 0;
            } else {
                prevBtn.disabled = currentPage === 0;
                nextBtn.disabled = currentPage >= pages.length - 1;
            }
        }

        function goToPage(idx, showIndicator = false) {
            if (idx < 0 || idx >= pages.length) return;
            
            const oldActive = container.querySelector(".page-wrapper.active");
            if (oldActive) oldActive.classList.remove("active");

            currentPage = idx;
            const wrapper = container.children[idx];
            if (wrapper) {
                wrapper.classList.add("active");
                if (singlePageMode) {
                    reader.scrollTop = 0;
                } else {
                    wrapper.scrollIntoView({ behavior: "smooth", block: "start" });
                }
            }

            if (showIndicator) {
                showToast(`${idx + 1} / ${pages.length}`);
            }

            updatePageInfo();
            updateNavButtons();
            preloadAhead(idx);
            savePosition();
            persistPrefs();
        }

        function nextPage() {
            const delta = rtlMode ? -1 : 1;
            const step = doublePageMode ? 2 : 1;
            const newPage = currentPage + (delta * step);
            if (newPage >= 0 && newPage < pages.length) {
                goToPage(newPage);
            }
        }

        function prevPage() {
            const delta = rtlMode ? -1 : 1;
            const step = doublePageMode ? 2 : 1;
            const newPage = currentPage - (delta * step);
            if (newPage >= 0 && newPage < pages.length) {
                goToPage(newPage);
            }
        }

        function toggleMode() {
            singlePageMode = !singlePageMode;
            reader.classList.toggle("single-page", singlePageMode);
            modeBtn.textContent = singlePageMode ? "Page" : "Scroll";
            
            if (singlePageMode) {
                reader.scrollTop = 0;
            } else {
                const wrapper = container.children[currentPage];
                if (wrapper) {
                    wrapper.scrollIntoView({ block: "start" });
                }
            }
            persistPrefs();
        }

        function toggleRtl() {
            rtlMode = !rtlMode;
            reader.classList.toggle("rtl", rtlMode);
            rtlBtn.classList.toggle("active", rtlMode);
            pageSlider.classList.toggle("rtl", rtlMode);
            updateNavButtons();
            persistPrefs();
        }

        function toggleDoublePage() {
            doublePageMode = !doublePageMode;
            reader.classList.toggle("double-page", doublePageMode);
            doubleBtn.classList.toggle("active", doublePageMode);
            persistPrefs();
        }

        function setFit(fit) {
            currentFit = fit;
            reader.className = reader.className.replace(/fit-\w+/g, "");
            reader.classList.add(`fit-${fit}`);
            if (singlePageMode) reader.classList.add("single-page");
            if (rtlMode) reader.classList.add("rtl");
            if (doublePageMode) reader.classList.add("double-page");
            
            fitBtn.textContent = fit === "width" ? "Fit Width" : fit === "height" ? "Fit Height" : "Original";
            
            fitDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.classList.toggle("active", item.dataset.fit === fit);
            });
            
            fitDropdown.classList.remove("open");
            persistPrefs();
        }

        function setZoom(zoom) {
            currentZoom = parseInt(zoom, 10);
            reader.dataset.zoom = currentZoom;
            zoomBtn.textContent = `${currentZoom}%`;
            
            zoomDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.classList.toggle("active", item.dataset.zoom === String(currentZoom));
            });
            
            zoomDropdown.classList.remove("open");
            persistPrefs();
        }

        function wireEvents() {
            prevBtn.onclick = prevPage;
            nextBtn.onclick = nextPage;
            modeBtn.onclick = toggleMode;
            rtlBtn.onclick = toggleRtl;
            doubleBtn.onclick = toggleDoublePage;

            fitBtn.onclick = () => fitDropdown.classList.toggle("open");
            zoomBtn.onclick = () => zoomDropdown.classList.toggle("open");

            fitDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setFit(item.dataset.fit);
            });

            zoomDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setZoom(item.dataset.zoom);
            });

            // Page slider
            pageSlider.oninput = () => {
                const page = parseInt(pageSlider.value, 10) - 1;
                goToPage(page, true);
            };

            // Close dropdowns on outside click
            document.addEventListener("click", (e) => {
                if (!fitDropdown.contains(e.target)) fitDropdown.classList.remove("open");
                if (!zoomDropdown.contains(e.target)) zoomDropdown.classList.remove("open");
            });

            // Keyboard navigation
            document.addEventListener("keydown", (e) => {
                const navRight = rtlMode ? prevPage : nextPage;
                const navLeft = rtlMode ? nextPage : prevPage;

                switch (e.key) {
                    case "ArrowRight":
                    case "d":
                    case "D":
                        navRight();
                        break;
                    case "ArrowLeft":
                    case "a":
                    case "A":
                        navLeft();
                        break;
                    case "Home":
                        goToPage(0);
                        break;
                    case "End":
                        goToPage(pages.length - 1);
                        break;
                    case "Escape":
                        fitDropdown.classList.remove("open");
                        zoomDropdown.classList.remove("open");
                        break;
                    case "r":
                    case "R":
                        toggleRtl();
                        break;
                    case "p":
                    case "P":
                        toggleDoublePage();
                        break;
                }
            });

            // Scroll-based page detection in scroll mode
            let scrollTimeout;
            reader.addEventListener("scroll", () => {
                if (singlePageMode) return;
                
                clearTimeout(scrollTimeout);
                scrollTimeout = setTimeout(() => {
                    const wrappers = container.children;
                    
                    for (let i = 0; i < wrappers.length; i++) {
                        const wrapper = wrappers[i];
                        const rect = wrapper.getBoundingClientRect();
                        const readerRect = reader.getBoundingClientRect();
                        
                        if (rect.top >= readerRect.top - 100 && rect.top < readerRect.bottom) {
                            if (currentPage !== i) {
                                const oldActive = container.querySelector(".page-wrapper.active");
                                if (oldActive) oldActive.classList.remove("active");
                                
                                currentPage = i;
                                wrapper.classList.add("active");
                                updatePageInfo();
                                updateNavButtons();
                                preloadAhead(i);
                            }
                            break;
                        }
                    }
                }, 100);
            });
        }

        function savePosition() {
            try {
                const key = `koware.reader.pos.${btoa(title).slice(0, 32)}`;
                localStorage.setItem(key, JSON.stringify({
                    page: currentPage,
                    total: pages.length,
                    savedAt: Date.now()
                }));
            } catch {}
        }

        function restorePosition() {
            try {
                const key = `koware.reader.pos.${btoa(title).slice(0, 32)}`;
                const raw = localStorage.getItem(key);
                if (raw) {
                    const { page, total, savedAt } = JSON.parse(raw);
                    // Only restore if saved within last 30 days and same chapter
                    if (Date.now() - savedAt < 30 * 24 * 60 * 60 * 1000 && total === pages.length && page > 0) {
                        goToPage(page, true);
                    }
                }
            } catch {}
        }

        function loadPrefs() {
            try {
                const raw = localStorage.getItem("koware.reader.prefs");
                if (raw) {
                    const prefs = JSON.parse(raw);
                    if (prefs.fit) setFit(prefs.fit);
                    if (prefs.zoom) setZoom(prefs.zoom);
                    if (prefs.singlePage && !singlePageMode) toggleMode();
                    if (prefs.rtl && !rtlMode) toggleRtl();
                    if (prefs.doublePage && !doublePageMode) toggleDoublePage();
                }
            } catch {}
        }

        function persistPrefs() {
            try {
                localStorage.setItem("koware.reader.prefs", JSON.stringify({
                    fit: currentFit,
                    zoom: currentZoom,
                    singlePage: singlePageMode,
                    rtl: rtlMode,
                    doublePage: doublePageMode
                }));
            } catch {}
        }

        // Save position on page close
        window.addEventListener("beforeunload", savePosition);

        init();
    </script>
</body>
</html>
""";

        return template
            .Replace("{{TITLE}}", encodedTitle)
            .Replace("{{PAGES_JSON}}", pagesJson)
            .Replace("{{TITLE_JSON}}", titleJson);
    }
}
