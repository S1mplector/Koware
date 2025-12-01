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
            background:
                radial-gradient(circle at 25% 25%, rgba(56, 189, 248, 0.08), transparent 26%),
                radial-gradient(circle at 80% 20%, rgba(248, 113, 113, 0.06), transparent 22%),
                var(--bg);
            color: var(--text);
            font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
            display: flex;
            flex-direction: column;
        }

        #header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 12px 20px;
            background: linear-gradient(145deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.01)) var(--panel);
            border-bottom: 1px solid var(--border);
            gap: 16px;
            flex-shrink: 0;
        }

        #title {
            font-weight: 700;
            font-size: 15px;
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
            gap: 8px;
            flex-shrink: 0;
        }

        .btn {
            border: 1px solid rgba(226, 232, 240, 0.15);
            background: rgba(226, 232, 240, 0.08);
            color: var(--text);
            border-radius: 10px;
            padding: 8px 14px;
            font-weight: 600;
            font-size: 13px;
            cursor: pointer;
            transition: background 0.2s ease, border-color 0.2s ease;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 6px;
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

        .btn-icon {
            padding: 8px;
            width: 38px;
            height: 38px;
        }

        #page-info {
            font-size: 13px;
            color: var(--muted);
            font-weight: 600;
            min-width: 80px;
            text-align: center;
        }

        #reader {
            flex: 1;
            overflow: auto;
            display: flex;
            justify-content: center;
            align-items: flex-start;
            padding: 20px;
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
            gap: 12px;
        }

        .page-wrapper {
            position: relative;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 200px;
            background: rgba(255, 255, 255, 0.02);
            border-radius: 8px;
            border: 1px solid var(--border);
            overflow: hidden;
        }

        .page-wrapper.loading::after {
            content: "";
            position: absolute;
            width: 32px;
            height: 32px;
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
            min-height: 120px;
            padding: 20px;
        }

        .page-wrapper.error::after {
            content: "Failed to load image";
            color: var(--error);
            font-size: 13px;
            font-weight: 600;
        }

        /* Fit modes */
        .fit-width .page-img {
            width: 100%;
            max-width: min(100%, 900px);
            height: auto;
        }

        .fit-height .page-wrapper {
            height: calc(100vh - 120px);
        }

        .fit-height .page-img {
            width: auto;
            max-height: calc(100vh - 140px);
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
            justify-content: center;
            padding: 10px 20px;
            background: linear-gradient(145deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.01)) var(--panel);
            border-top: 1px solid var(--border);
            gap: 12px;
            flex-shrink: 0;
        }

        .nav-btn {
            padding: 10px 20px;
        }

        kbd {
            display: inline-block;
            padding: 2px 6px;
            font-size: 11px;
            font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
            background: rgba(255, 255, 255, 0.08);
            border: 1px solid var(--border);
            border-radius: 4px;
            color: var(--muted);
            margin-left: 4px;
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
            border-radius: 10px;
            padding: 6px;
            min-width: 140px;
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
            padding: 8px 12px;
            background: transparent;
            border: none;
            color: var(--text);
            font-size: 13px;
            text-align: left;
            cursor: pointer;
            border-radius: 6px;
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
    </style>
</head>
<body>
    <div id="progress-bar" style="width: 0%"></div>
    <header id="header">
        <div id="title">{{TITLE}}</div>
        <div class="controls">
            <div class="dropdown" id="fit-dropdown">
                <button class="btn" id="fit-btn" type="button">Fit Width</button>
                <div class="dropdown-menu">
                    <button class="dropdown-item active" data-fit="width">Fit Width</button>
                    <button class="dropdown-item" data-fit="height">Fit Height</button>
                    <button class="dropdown-item" data-fit="original">Original Size</button>
                </div>
            </div>
            <div class="dropdown" id="zoom-dropdown">
                <button class="btn" id="zoom-btn" type="button">100%</button>
                <div class="dropdown-menu">
                    <button class="dropdown-item active" data-zoom="100">100%</button>
                    <button class="dropdown-item" data-zoom="125">125%</button>
                    <button class="dropdown-item" data-zoom="150">150%</button>
                    <button class="dropdown-item" data-zoom="175">175%</button>
                    <button class="dropdown-item" data-zoom="200">200%</button>
                </div>
            </div>
            <button class="btn" id="mode-btn" type="button">Scroll Mode</button>
        </div>
    </header>
    <main id="reader" class="fit-width" data-zoom="100">
        <div id="pages-container"></div>
    </main>
    <footer id="footer">
        <button class="btn nav-btn" id="prev-btn" type="button" disabled>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="15 18 9 12 15 6"></polyline></svg>
            Previous <kbd>←</kbd>
        </button>
        <span id="page-info">1 / 1</span>
        <button class="btn nav-btn" id="next-btn" type="button">
            Next <kbd>→</kbd>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 18 15 12 9 6"></polyline></svg>
        </button>
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

        let currentPage = 0;
        let singlePageMode = false;
        let currentFit = "width";
        let currentZoom = 100;
        let loadedCount = 0;

        function init() {
            renderPages();
            updatePageInfo();
            updateNavButtons();
            wireEvents();
            loadPrefs();
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
                img.loading = idx < 3 ? "eager" : "lazy";
                
                img.onload = () => {
                    wrapper.classList.remove("loading");
                    img.classList.remove("loading");
                    loadedCount++;
                    updateProgress();
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

        function updateProgress() {
            const pct = pages.length > 0 ? (loadedCount / pages.length) * 100 : 0;
            progressBar.style.width = `${pct}%`;
            if (pct >= 100) {
                setTimeout(() => { progressBar.style.opacity = "0"; }, 500);
            }
        }

        function updatePageInfo() {
            const total = pages.length;
            const current = currentPage + 1;
            pageInfo.textContent = `${current} / ${total}`;
        }

        function updateNavButtons() {
            prevBtn.disabled = currentPage === 0;
            nextBtn.disabled = currentPage >= pages.length - 1;
        }

        function goToPage(idx) {
            if (idx < 0 || idx >= pages.length) return;
            
            // Remove active from old
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

            updatePageInfo();
            updateNavButtons();
            persistPrefs();
        }

        function nextPage() {
            if (currentPage < pages.length - 1) {
                goToPage(currentPage + 1);
            }
        }

        function prevPage() {
            if (currentPage > 0) {
                goToPage(currentPage - 1);
            }
        }

        function toggleMode() {
            singlePageMode = !singlePageMode;
            reader.classList.toggle("single-page", singlePageMode);
            modeBtn.textContent = singlePageMode ? "Single Page" : "Scroll Mode";
            
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

        function setFit(fit) {
            currentFit = fit;
            reader.className = reader.className.replace(/fit-\w+/g, "");
            reader.classList.add(`fit-${fit}`);
            if (singlePageMode) reader.classList.add("single-page");
            
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

            fitBtn.onclick = () => fitDropdown.classList.toggle("open");
            zoomBtn.onclick = () => zoomDropdown.classList.toggle("open");

            fitDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setFit(item.dataset.fit);
            });

            zoomDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setZoom(item.dataset.zoom);
            });

            // Close dropdowns on outside click
            document.addEventListener("click", (e) => {
                if (!fitDropdown.contains(e.target)) fitDropdown.classList.remove("open");
                if (!zoomDropdown.contains(e.target)) zoomDropdown.classList.remove("open");
            });

            // Keyboard navigation
            document.addEventListener("keydown", (e) => {
                if (e.key === "ArrowRight" || e.key === "d" || e.key === "D") {
                    nextPage();
                } else if (e.key === "ArrowLeft" || e.key === "a" || e.key === "A") {
                    prevPage();
                } else if (e.key === "Home") {
                    goToPage(0);
                } else if (e.key === "End") {
                    goToPage(pages.length - 1);
                } else if (e.key === "Escape") {
                    fitDropdown.classList.remove("open");
                    zoomDropdown.classList.remove("open");
                }
            });

            // Scroll-based page detection in scroll mode
            let scrollTimeout;
            reader.addEventListener("scroll", () => {
                if (singlePageMode) return;
                
                clearTimeout(scrollTimeout);
                scrollTimeout = setTimeout(() => {
                    const scrollTop = reader.scrollTop;
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
                            }
                            break;
                        }
                    }
                }, 100);
            });
        }

        function loadPrefs() {
            try {
                const raw = localStorage.getItem("koware.reader.prefs");
                if (raw) {
                    const prefs = JSON.parse(raw);
                    if (prefs.fit) setFit(prefs.fit);
                    if (prefs.zoom) setZoom(prefs.zoom);
                    if (prefs.singlePage !== undefined && prefs.singlePage !== singlePageMode) {
                        toggleMode();
                    }
                }
            } catch {}
        }

        function persistPrefs() {
            try {
                localStorage.setItem("koware.reader.prefs", JSON.stringify({
                    fit: currentFit,
                    zoom: currentZoom,
                    singlePage: singlePageMode
                }));
            } catch {}
        }

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
