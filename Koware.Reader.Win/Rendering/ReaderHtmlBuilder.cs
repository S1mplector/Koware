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
            --img-filter: none;
        }

        /* Sepia theme */
        body.theme-sepia {
            --bg: #f4ecd8;
            --panel: #e8dfc9;
            --border: rgba(139, 119, 89, 0.2);
            --text: #5c4b37;
            --muted: #8b7759;
            --accent: #b8860b;
            --img-filter: sepia(30%) brightness(0.95);
            color-scheme: light;
        }

        /* Light theme */
        body.theme-light {
            --bg: #f8fafc;
            --panel: #ffffff;
            --border: rgba(100, 116, 139, 0.15);
            --text: #1e293b;
            --muted: #64748b;
            --accent: #0ea5e9;
            --img-filter: none;
            color-scheme: light;
        }

        /* High contrast theme */
        body.theme-contrast {
            --bg: #000000;
            --panel: #0a0a0a;
            --border: rgba(255, 255, 255, 0.3);
            --text: #ffffff;
            --muted: #cccccc;
            --accent: #00ff88;
            --img-filter: contrast(1.1) brightness(1.05);
            color-scheme: dark;
        }

        /* Monokai theme */
        body.theme-monokai {
            --bg: #272822;
            --panel: #1e1f1c;
            --border: rgba(166, 226, 46, 0.15);
            --text: #f8f8f2;
            --muted: #75715e;
            --accent: #a6e22e;
            --img-filter: sepia(10%) saturate(1.1) hue-rotate(-10deg);
            color-scheme: dark;
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
            position: relative;
            z-index: 100;
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
            transition: opacity 0.2s ease, filter 0.3s ease;
            filter: var(--img-filter);
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

        /* Zoom - use width scaling instead of transform to allow proper scrolling */
        #reader[data-zoom="125"] .page-img { width: 125%; max-width: none; }
        #reader[data-zoom="150"] .page-img { width: 150%; max-width: none; }
        #reader[data-zoom="175"] .page-img { width: 175%; max-width: none; }
        #reader[data-zoom="200"] .page-img { width: 200%; max-width: none; }
        
        #reader[data-zoom="125"] .page-wrapper,
        #reader[data-zoom="150"] .page-wrapper,
        #reader[data-zoom="175"] .page-wrapper,
        #reader[data-zoom="200"] .page-wrapper {
            max-width: none;
            width: auto;
        }

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
            transition: background 0.3s ease, border-color 0.3s ease, box-shadow 0.3s ease, transform 0.2s ease;
        }

        .nav-btn:hover:not(:disabled) {
            box-shadow: 0 0 16px rgba(56, 189, 248, 0.5), 0 0 32px rgba(56, 189, 248, 0.25);
            border-color: rgba(56, 189, 248, 0.6);
            background: rgba(56, 189, 248, 0.15);
            transform: scale(1.05);
        }

        .nav-btn:active:not(:disabled) {
            transform: scale(0.98);
            box-shadow: 0 0 8px rgba(56, 189, 248, 0.4);
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

        /* Chapters panel */
        #chapters-panel {
            position: fixed;
            top: 0;
            right: -320px;
            width: 320px;
            height: 100%;
            background: var(--panel);
            border-left: 1px solid var(--border);
            z-index: 200;
            transition: right 0.3s ease;
            display: flex;
            flex-direction: column;
        }

        #chapters-panel.open {
            right: 0;
        }

        #chapters-panel-header {
            padding: 16px;
            border-bottom: 1px solid var(--border);
            display: flex;
            align-items: center;
            justify-content: space-between;
        }

        #chapters-panel-header h3 {
            margin: 0;
            font-size: 16px;
            font-weight: 700;
        }

        #chapters-panel-close {
            background: none;
            border: none;
            color: var(--muted);
            cursor: pointer;
            padding: 4px;
            font-size: 20px;
            line-height: 1;
        }

        #chapters-panel-close:hover {
            color: var(--text);
        }

        #chapters-list {
            flex: 1;
            overflow-y: auto;
            padding: 8px;
        }

        .chapter-item {
            display: flex;
            align-items: center;
            padding: 10px 12px;
            border-radius: 8px;
            cursor: pointer;
            transition: background 0.15s ease;
            gap: 10px;
        }

        .chapter-item:hover {
            background: rgba(255, 255, 255, 0.05);
        }

        .chapter-item.current {
            background: rgba(56, 189, 248, 0.15);
            border: 1px solid rgba(56, 189, 248, 0.3);
        }

        .chapter-item.read {
            opacity: 0.6;
        }

        .chapter-number {
            font-weight: 700;
            font-size: 13px;
            min-width: 50px;
            color: var(--accent);
        }

        .chapter-title {
            flex: 1;
            font-size: 13px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        .chapter-read-badge {
            font-size: 10px;
            padding: 2px 6px;
            border-radius: 4px;
            background: rgba(56, 189, 248, 0.2);
            color: var(--accent);
        }

        /* Chapter nav buttons in footer */
        .chapter-nav-btn {
            padding: 6px 10px !important;
            font-size: 11px !important;
        }

        .chapter-nav-btn:disabled {
            opacity: 0.3;
        }

        /* Overlay when panel is open */
        #chapters-overlay {
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background: rgba(0, 0, 0, 0.5);
            z-index: 150;
            opacity: 0;
            pointer-events: none;
            transition: opacity 0.3s ease;
        }

        #chapters-overlay.visible {
            opacity: 1;
            pointer-events: auto;
        }

        /* Zen mode - distraction-free reading */
        body.zen-mode #header,
        body.zen-mode #footer {
            opacity: 0;
            pointer-events: none;
            transform: translateY(-100%);
            transition: opacity 0.3s ease, transform 0.3s ease;
        }

        body.zen-mode #footer {
            transform: translateY(100%);
        }

        body.zen-mode #reader {
            height: 100vh;
        }

        body.zen-mode.controls-visible #header,
        body.zen-mode.controls-visible #footer {
            opacity: 1;
            pointer-events: auto;
            transform: translateY(0);
        }

        #header, #footer {
            transition: opacity 0.3s ease, transform 0.3s ease;
        }

        /* Zen mode indicator toast */
        #zen-toast {
            position: fixed;
            bottom: 20px;
            left: 50%;
            transform: translateX(-50%);
            background: rgba(0, 0, 0, 0.85);
            color: var(--text);
            padding: 10px 20px;
            border-radius: 8px;
            font-weight: 600;
            font-size: 13px;
            pointer-events: none;
            opacity: 0;
            transition: opacity 0.2s ease;
            z-index: 100;
        }

        #zen-toast.visible {
            opacity: 1;
        }
    </style>
</head>
<body>
    <div id="page-toast"></div>
    <div id="zen-toast"></div>
    <div id="chapters-overlay"></div>
    <div id="chapters-panel">
        <div id="chapters-panel-header">
            <h3>Chapters</h3>
            <button id="chapters-panel-close" type="button">&times;</button>
        </div>
        <div id="chapters-list"></div>
    </div>
    <header id="header">
        <div id="title">{{TITLE}}</div>
        <div class="controls">
            <button class="btn btn-sm" id="chapters-btn" type="button" title="Chapters list">Chapters <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 6h16M4 12h16M4 18h16"/></svg></button>
            <button class="btn btn-sm" id="rtl-btn" type="button" title="Right-to-Left reading">RTL <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M17 4H10c-2.76 0-5 2.24-5 5s2.24 5 5 5h1M7 14l-4 4 4 4"/><path d="M17 4v16"/></svg></button>
            <button class="btn btn-sm" id="double-btn" type="button" title="Double page spread">2-Page <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H12V3H6.5A2.5 2.5 0 0 0 4 5.5v14z"/><path d="M20 19.5A2.5 2.5 0 0 0 17.5 17H12V3h5.5A2.5 2.5 0 0 1 20 5.5v14z"/></svg></button>
            <div class="dropdown" id="fit-dropdown">
                <button class="btn btn-sm" id="fit-btn" type="button">Fit Width <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12H3M21 12l-4-4m4 4l-4 4M3 12l4-4m-4 4l4 4"/></svg></button>
                <div class="dropdown-menu">
                    <button class="dropdown-item active" data-fit="width">Fit Width</button>
                    <button class="dropdown-item" data-fit="height">Fit Height</button>
                    <button class="dropdown-item" data-fit="original">Original Size</button>
                </div>
            </div>
            <div class="dropdown" id="zoom-dropdown">
                <button class="btn btn-sm" id="zoom-btn" type="button">100% <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="M21 21l-4.35-4.35"/><path d="M11 8v6M8 11h6"/></svg></button>
                <div class="dropdown-menu">
                    <button class="dropdown-item active" data-zoom="100">100%</button>
                    <button class="dropdown-item" data-zoom="125">125%</button>
                    <button class="dropdown-item" data-zoom="150">150%</button>
                    <button class="dropdown-item" data-zoom="175">175%</button>
                    <button class="dropdown-item" data-zoom="200">200%</button>
                </div>
            </div>
            <button class="btn btn-sm" id="mode-btn" type="button">Scroll <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3v18M12 3l4 4M12 3L8 7M12 21l4-4M12 21l-4-4"/></svg></button>
            <button class="btn btn-sm" id="zen-btn" type="button" title="Zen Mode (Z) - Hide UI for distraction-free reading">Zen <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></svg></button>
            <div class="dropdown" id="theme-dropdown">
                <button class="btn btn-sm" id="theme-btn" type="button">Theme <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="5"/><path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/></svg></button>
                <div class="dropdown-menu">
                    <button class="dropdown-item active" data-theme="dark">Dark</button>
                    <button class="dropdown-item" data-theme="sepia">Sepia</button>
                    <button class="dropdown-item" data-theme="light">Light</button>
                    <button class="dropdown-item" data-theme="contrast">High Contrast</button>
                    <button class="dropdown-item" data-theme="monokai">Monokai</button>
                </div>
            </div>
        </div>
    </header>
    <main id="reader" class="fit-width" data-zoom="100">
        <div id="pages-container"></div>
    </main>
    <footer id="footer">
        <div class="nav-controls">
            <button class="btn chapter-nav-btn" id="prev-chapter-btn" type="button" disabled title="Previous Chapter">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="11 17 6 12 11 7"></polyline><polyline points="18 17 13 12 18 7"></polyline></svg>
            </button>
            <button class="btn nav-btn" id="prev-btn" type="button" disabled>
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="15 18 9 12 15 6"></polyline></svg>
            </button>
        </div>
        <div id="page-slider-container">
            <span id="page-info">1 / 1</span>
            <input type="range" id="page-slider" min="1" max="1" value="1" />
        </div>
        <div class="nav-controls">
            <button class="btn nav-btn" id="next-btn" type="button">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="9 18 15 12 9 6"></polyline></svg>
            </button>
            <button class="btn chapter-nav-btn" id="next-chapter-btn" type="button" disabled title="Next Chapter">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="13 17 18 12 13 7"></polyline><polyline points="6 17 11 12 6 7"></polyline></svg>
            </button>
        </div>
    </footer>
    <script>
        const pages = {{PAGES_JSON}};
        const title = {{TITLE_JSON}};
        const chapters = {{CHAPTERS_JSON}};
        const navPath = {{NAV_PATH_JSON}};
        const reader = document.getElementById("reader");
        const container = document.getElementById("pages-container");
        const pageInfo = document.getElementById("page-info");
        const prevBtn = document.getElementById("prev-btn");
        const nextBtn = document.getElementById("next-btn");
        const prevChapterBtn = document.getElementById("prev-chapter-btn");
        const nextChapterBtn = document.getElementById("next-chapter-btn");
        const chaptersBtn = document.getElementById("chapters-btn");
        const chaptersPanel = document.getElementById("chapters-panel");
        const chaptersPanelClose = document.getElementById("chapters-panel-close");
        const chaptersList = document.getElementById("chapters-list");
        const chaptersOverlay = document.getElementById("chapters-overlay");
        const modeBtn = document.getElementById("mode-btn");
        const fitBtn = document.getElementById("fit-btn");
        const fitDropdown = document.getElementById("fit-dropdown");
        const zoomBtn = document.getElementById("zoom-btn");
        const zoomDropdown = document.getElementById("zoom-dropdown");
        const pageSlider = document.getElementById("page-slider");
        const pageToast = document.getElementById("page-toast");
        const rtlBtn = document.getElementById("rtl-btn");
        const doubleBtn = document.getElementById("double-btn");
        const themeBtn = document.getElementById("theme-btn");
        const themeDropdown = document.getElementById("theme-dropdown");
        const zenBtn = document.getElementById("zen-btn");
        const zenToast = document.getElementById("zen-toast");

        let currentPage = 0;
        let currentTheme = "dark";
        let singlePageMode = false;
        let rtlMode = false;
        let doublePageMode = false;
        let currentFit = "width";
        let currentZoom = 100;
        let loadedCount = 0;
        let toastTimeout = null;
        let zenMode = false;
        let zenHideTimeout = null;
        let zenToastTimeout = null;

        function init() {
            pageSlider.max = pages.length;
            renderPages();
            updatePageInfo();
            updateNavButtons();
            wireEvents();
            loadPrefs();
            restorePosition();
            initChapters();
            initZenMode();
        }

        // ===== Chapter Navigation =====
        
        function initChapters() {
            if (!chapters || chapters.length === 0) {
                chaptersBtn.style.display = "none";
                prevChapterBtn.style.display = "none";
                nextChapterBtn.style.display = "none";
                return;
            }
            renderChaptersList();
            updateChapterNavButtons();
        }
        
        function getCurrentChapterIndex() {
            const idx = chapters.findIndex(c => c.current);
            return idx >= 0 ? idx : 0;
        }
        
        function renderChaptersList() {
            chaptersList.innerHTML = "";
            const currentIdx = getCurrentChapterIndex();
            chapters.forEach((chapter, idx) => {
                const item = document.createElement("div");
                item.className = "chapter-item" + (idx === currentIdx ? " current" : "") + (chapter.read ? " read" : "");
                item.innerHTML = `<span class="chapter-number">Ch. ${chapter.number}</span><span class="chapter-title">${chapter.title || "Chapter " + chapter.number}</span>${chapter.read ? '<span class="chapter-read-badge">Read</span>' : ""}`;
                item.onclick = () => navigateToChapter(idx);
                chaptersList.appendChild(item);
            });
        }
        
        function updateChapterNavButtons() {
            const idx = getCurrentChapterIndex();
            prevChapterBtn.disabled = idx <= 0;
            nextChapterBtn.disabled = idx >= chapters.length - 1;
        }
        
        function toggleChaptersPanel(open) {
            const isOpen = typeof open === "boolean" ? open : !chaptersPanel.classList.contains("open");
            chaptersPanel.classList.toggle("open", isOpen);
            chaptersOverlay.classList.toggle("visible", isOpen);
        }
        
        function navigateToChapter(targetIdx) {
            if (targetIdx === getCurrentChapterIndex()) { toggleChaptersPanel(false); return; }
            const targetChapter = chapters[targetIdx];
            if (targetChapter) {
                writeNavAndClose("goto:" + targetChapter.number);
            }
        }
        
        function writeNavAndClose(direction) {
            if (navPath && window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage({ type: "nav", direction: direction, path: navPath });
            }
            window.__navResult = direction;
            // Window will be closed by C# after receiving the message
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
                    // Preload next pages when current loads
                    preloadAhead(idx);
                };
                
                img.onerror = () => {
                    wrapper.classList.remove("loading");
                    wrapper.classList.add("error");
                    img.style.display = "none";
                    loadedCount++;
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

        const scrollIcon = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3v18M12 3l4 4M12 3L8 7M12 21l4-4M12 21l-4-4"/></svg>';
        const pageIcon = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/></svg>';

        function toggleMode() {
            singlePageMode = !singlePageMode;
            reader.classList.toggle("single-page", singlePageMode);
            modeBtn.innerHTML = singlePageMode ? "Page " + pageIcon : "Scroll " + scrollIcon;
            
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

        const fitWidthIcon = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12H3M21 12l-4-4m4 4l-4 4M3 12l4-4m-4 4l4 4"/></svg>';
        const fitHeightIcon = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3v18M12 3l-4 4m4-4l4 4M12 21l-4-4m4 4l4-4"/></svg>';
        const originalIcon = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="12" cy="12" r="3"/></svg>';
        const zoomIcon = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="M21 21l-4.35-4.35"/><path d="M11 8v6M8 11h6"/></svg>';

        function setFit(fit) {
            currentFit = fit;
            reader.className = reader.className.replace(/fit-\w+/g, "");
            reader.classList.add(`fit-${fit}`);
            if (singlePageMode) reader.classList.add("single-page");
            if (rtlMode) reader.classList.add("rtl");
            if (doublePageMode) reader.classList.add("double-page");
            
            const fitText = fit === "width" ? "Fit Width" : fit === "height" ? "Fit Height" : "Original";
            const fitIcon = fit === "width" ? fitWidthIcon : fit === "height" ? fitHeightIcon : originalIcon;
            fitBtn.innerHTML = fitText + " " + fitIcon;
            
            fitDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.classList.toggle("active", item.dataset.fit === fit);
            });
            
            fitDropdown.classList.remove("open");
            persistPrefs();
        }

        function setZoom(zoom) {
            currentZoom = parseInt(zoom, 10);
            reader.dataset.zoom = currentZoom;
            zoomBtn.innerHTML = `${currentZoom}% ` + zoomIcon;
            
            zoomDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.classList.toggle("active", item.dataset.zoom === String(currentZoom));
            });
            
            zoomDropdown.classList.remove("open");
            persistPrefs();
        }

        const themeIcon = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="5"/><path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/></svg>';

        function setTheme(theme) {
            currentTheme = theme;
            document.body.className = document.body.className.replace(/theme-\w+/g, "");
            if (theme !== "dark") {
                document.body.classList.add(`theme-${theme}`);
            }
            
            const themeNames = { dark: "Dark", sepia: "Sepia", light: "Light", contrast: "High Contrast", monokai: "Monokai" };
            themeBtn.innerHTML = (themeNames[theme] || "Theme") + " " + themeIcon;
            
            themeDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.classList.toggle("active", item.dataset.theme === theme);
            });
            
            themeDropdown.classList.remove("open");
            persistPrefs();
        }

        function wireEvents() {
            prevBtn.onclick = prevPage;
            nextBtn.onclick = nextPage;
            modeBtn.onclick = toggleMode;
            rtlBtn.onclick = toggleRtl;
            doubleBtn.onclick = toggleDoublePage;
            zenBtn.onclick = toggleZenMode;

            fitBtn.onclick = () => fitDropdown.classList.toggle("open");
            zoomBtn.onclick = () => zoomDropdown.classList.toggle("open");

            fitDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setFit(item.dataset.fit);
            });

            zoomDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setZoom(item.dataset.zoom);
            });

            themeBtn.onclick = () => themeDropdown.classList.toggle("open");
            themeDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setTheme(item.dataset.theme);
            });

            // Chapter navigation
            chaptersBtn.onclick = () => toggleChaptersPanel();
            chaptersPanelClose.onclick = () => toggleChaptersPanel(false);
            chaptersOverlay.onclick = () => toggleChaptersPanel(false);
            prevChapterBtn.onclick = () => writeNavAndClose("prev");
            nextChapterBtn.onclick = () => writeNavAndClose("next");

            // Page slider
            pageSlider.oninput = () => {
                const page = parseInt(pageSlider.value, 10) - 1;
                goToPage(page, true);
            };

            // Close dropdowns on outside click
            document.addEventListener("click", (e) => {
                if (!fitDropdown.contains(e.target)) fitDropdown.classList.remove("open");
                if (!zoomDropdown.contains(e.target)) zoomDropdown.classList.remove("open");
                if (!themeDropdown.contains(e.target)) themeDropdown.classList.remove("open");
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
                        themeDropdown.classList.remove("open");
                        break;
                    case "r":
                    case "R":
                        toggleRtl();
                        break;
                    case "p":
                    case "P":
                        toggleDoublePage();
                        break;
                    case "z":
                    case "Z":
                        toggleZenMode();
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
                    if (prefs.theme) setTheme(prefs.theme);
                    if (prefs.singlePage && !singlePageMode) toggleMode();
                    if (prefs.rtl && !rtlMode) toggleRtl();
                    if (prefs.doublePage && !doublePageMode) toggleDoublePage();
                    if (prefs.zenMode && !zenMode) toggleZenMode();
                }
            } catch {}
        }

        function persistPrefs() {
            try {
                localStorage.setItem("koware.reader.prefs", JSON.stringify({
                    fit: currentFit,
                    zoom: currentZoom,
                    theme: currentTheme,
                    singlePage: singlePageMode,
                    rtl: rtlMode,
                    doublePage: doublePageMode,
                    zenMode: zenMode
                }));
            } catch {}
        }

        // ===== Zen Mode =====
        
        function initZenMode() {
            // Mouse movement shows controls temporarily in zen mode
            document.addEventListener("mousemove", onZenMouseMove);
            document.addEventListener("mouseleave", () => {
                if (zenMode) scheduleZenHide();
            });
        }
        
        function onZenMouseMove() {
            if (!zenMode) return;
            showZenControls();
            scheduleZenHide();
        }
        
        function showZenControls() {
            document.body.classList.add("controls-visible");
        }
        
        function hideZenControls() {
            document.body.classList.remove("controls-visible");
        }
        
        function scheduleZenHide() {
            clearTimeout(zenHideTimeout);
            zenHideTimeout = setTimeout(hideZenControls, 2500);
        }
        
        function showZenToast(text) {
            zenToast.textContent = text;
            zenToast.classList.add("visible");
            clearTimeout(zenToastTimeout);
            zenToastTimeout = setTimeout(() => zenToast.classList.remove("visible"), 1500);
        }
        
        function toggleZenMode() {
            zenMode = !zenMode;
            document.body.classList.toggle("zen-mode", zenMode);
            zenBtn.classList.toggle("active", zenMode);
            
            if (zenMode) {
                showZenToast("Zen Mode ON — Move mouse to show controls");
                scheduleZenHide();
            } else {
                hideZenControls();
                clearTimeout(zenHideTimeout);
                showZenToast("Zen Mode OFF");
            }
            
            persistPrefs();
        }

        // Save position on page close
        window.addEventListener("beforeunload", savePosition);

        init();
    </script>
</body>
</html>
""";

        var chaptersJson = string.IsNullOrWhiteSpace(args.ChaptersJson) ? "[]" : args.ChaptersJson;
        var navPathJson = JsonSerializer.Serialize(args.NavResultPath ?? "");

        return template
            .Replace("{{TITLE}}", encodedTitle)
            .Replace("{{PAGES_JSON}}", pagesJson)
            .Replace("{{TITLE_JSON}}", titleJson)
            .Replace("{{CHAPTERS_JSON}}", chaptersJson)
            .Replace("{{NAV_PATH_JSON}}", navPathJson);
    }
}
