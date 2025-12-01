// Author: Ilgaz Mehmetoğlu
// Builds the embedded HTML/JS page used inside the WebView player.
using System.Net;
using System.Text.Json;
using Koware.Player.Win.Startup;

namespace Koware.Player.Win.Rendering;

internal static class HtmlPageBuilder
{
    public static string Build(PlayerArguments args)
    {
        var urlJson = JsonSerializer.Serialize(args.Url.ToString());
        var titleJson = JsonSerializer.Serialize(args.Title);
        var subtitleJson = JsonSerializer.Serialize(args.SubtitleUrl?.ToString());
        var subtitleLabelJson = JsonSerializer.Serialize(args.SubtitleLabel);
        var encodedTitle = WebUtility.HtmlEncode(args.Title);

        const string template = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover" />
    <title>{{TITLE}}</title>
    <script src="https://cdn.jsdelivr.net/npm/hls.js@1.5.11/dist/hls.min.js"></script>
    <style id="subtitle-style"></style>
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

        * { box-sizing: border-box; }

        body {
            margin: 0;
            padding: 0;
            background: var(--bg);
            color: var(--text);
            font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
            height: 100vh;
            display: flex;
            flex-direction: column;
            overflow: hidden;
        }

        body:not(.fullscreen) {
            padding: 16px;
        }

        #chrome {
            flex: 1;
            display: flex;
            flex-direction: column;
            background: linear-gradient(145deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.01)) var(--panel);
            border: 1px solid var(--border);
            border-radius: 16px;
            overflow: hidden;
            box-shadow: 0 18px 45px rgba(0, 0, 0, 0.35);
        }

        body.fullscreen #chrome {
            border-radius: 0;
            border: none;
        }

        #header {
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 12px 16px;
            border-bottom: 1px solid var(--border);
            flex-shrink: 0;
        }

        body.fullscreen #header {
            position: absolute;
            top: 0;
            left: 0;
            right: 0;
            z-index: 10;
            background: linear-gradient(to bottom, rgba(0,0,0,0.7), transparent);
            border: none;
            opacity: 0;
            transition: opacity 0.3s ease;
        }

        body.fullscreen:hover #header,
        body.fullscreen.controls-visible #header {
            opacity: 1;
        }

        #title {
            font-weight: 700;
            font-size: 14px;
            letter-spacing: 0.01em;
            color: var(--text);
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }

        #player-wrapper {
            position: relative;
            flex: 1;
            display: flex;
            align-items: center;
            justify-content: center;
            background: #0b1221;
            overflow: hidden;
        }

        video {
            width: 100%;
            height: 100%;
            display: block;
            object-fit: contain;
            background: #0b1221;
        }

        #status {
            position: absolute;
            inset: 0;
            display: grid;
            place-items: center;
            font-weight: 600;
            color: var(--muted);
            pointer-events: none;
            text-shadow: 0 1px 8px rgba(0, 0, 0, 0.35);
            transition: opacity 0.25s ease;
        }

        /* Top controls (CC, Settings) */
        #top-controls {
            position: absolute;
            top: 12px;
            right: 12px;
            display: flex;
            gap: 8px;
            z-index: 5;
            opacity: 0;
            transition: opacity 0.3s ease;
        }

        #player-wrapper:hover #top-controls,
        body.controls-visible #top-controls {
            opacity: 1;
        }

        .ctrl-btn {
            border: 1px solid rgba(226, 232, 240, 0.2);
            background: rgba(0, 0, 0, 0.6);
            color: var(--text);
            border-radius: 8px;
            padding: 6px 10px;
            font-weight: 600;
            font-size: 12px;
            cursor: pointer;
            transition: background 0.2s ease;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 4px;
        }

        .ctrl-btn:hover {
            background: rgba(0, 0, 0, 0.8);
        }

        .ctrl-btn:disabled {
            opacity: 0.5;
            cursor: not-allowed;
        }

        .ctrl-btn.active {
            background: rgba(56, 189, 248, 0.3);
            border-color: rgba(56, 189, 248, 0.5);
        }

        /* Bottom control bar */
        #control-bar {
            display: flex;
            align-items: center;
            gap: 12px;
            padding: 10px 16px;
            background: linear-gradient(145deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.01)) var(--panel);
            border-top: 1px solid var(--border);
            flex-shrink: 0;
        }

        body.fullscreen #control-bar {
            position: absolute;
            bottom: 0;
            left: 0;
            right: 0;
            z-index: 10;
            background: linear-gradient(to top, rgba(0,0,0,0.8), transparent);
            border: none;
            padding: 20px 16px 16px;
            opacity: 0;
            transition: opacity 0.3s ease;
        }

        body.fullscreen:hover #control-bar,
        body.fullscreen.controls-visible #control-bar {
            opacity: 1;
        }

        #progress-container {
            flex: 1;
            height: 6px;
            background: rgba(255, 255, 255, 0.1);
            border-radius: 3px;
            cursor: pointer;
            position: relative;
        }

        #progress-bar {
            height: 100%;
            background: var(--accent);
            border-radius: 3px;
            width: 0%;
            transition: width 0.1s linear;
        }

        #buffer-bar {
            position: absolute;
            top: 0;
            left: 0;
            height: 100%;
            background: rgba(255, 255, 255, 0.2);
            border-radius: 3px;
            pointer-events: none;
        }

        #time-display {
            font-size: 12px;
            color: var(--muted);
            font-weight: 600;
            min-width: 90px;
            text-align: center;
        }

        .icon-btn {
            background: transparent;
            border: none;
            color: var(--text);
            cursor: pointer;
            padding: 6px;
            border-radius: 6px;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            transition: background 0.2s ease;
        }

        .icon-btn:hover {
            background: rgba(255, 255, 255, 0.1);
        }

        .icon-btn svg {
            width: 20px;
            height: 20px;
        }

        #volume-container {
            display: flex;
            align-items: center;
            gap: 6px;
        }

        #volume-slider {
            width: 80px;
            height: 4px;
            -webkit-appearance: none;
            background: rgba(255, 255, 255, 0.2);
            border-radius: 2px;
            cursor: pointer;
        }

        #volume-slider::-webkit-slider-thumb {
            -webkit-appearance: none;
            width: 12px;
            height: 12px;
            background: var(--text);
            border-radius: 50%;
            cursor: pointer;
        }

        /* Speed dropdown */
        .dropdown {
            position: relative;
        }

        .dropdown-menu {
            position: absolute;
            bottom: 100%;
            left: 50%;
            transform: translateX(-50%);
            margin-bottom: 8px;
            background: rgba(15, 23, 42, 0.98);
            border: 1px solid var(--border);
            border-radius: 10px;
            padding: 6px;
            min-width: 100px;
            display: none;
            z-index: 20;
            box-shadow: 0 12px 32px rgba(0, 0, 0, 0.5);
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
            text-align: center;
            cursor: pointer;
            border-radius: 6px;
            transition: background 0.15s ease;
        }

        .dropdown-item:hover {
            background: rgba(255, 255, 255, 0.1);
        }

        .dropdown-item.active {
            background: rgba(56, 189, 248, 0.2);
            color: var(--accent);
        }

        #speed-btn {
            font-size: 12px;
            font-weight: 600;
            min-width: 45px;
        }

        /* Settings panel */
        #settings-panel {
            position: absolute;
            top: 50px;
            right: 12px;
            width: 260px;
            background: rgba(15, 23, 42, 0.98);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 12px;
            display: none;
            gap: 10px;
            z-index: 15;
            box-shadow: 0 16px 36px rgba(0, 0, 0, 0.5);
        }

        #settings-panel[data-open="true"] {
            display: grid;
        }

        #settings-panel h3 {
            margin: 0 0 8px 0;
            font-size: 14px;
            letter-spacing: 0.01em;
            color: var(--text);
        }

        .setting {
            display: grid;
            gap: 4px;
        }

        .setting label {
            font-size: 12px;
            color: var(--muted);
        }

        .setting .value {
            font-size: 12px;
            color: var(--text);
            justify-self: end;
        }

        .setting-row {
            display: grid;
            grid-template-columns: 1fr auto;
            align-items: center;
            gap: 6px;
        }

        input[type="range"] {
            width: 100%;
        }

        select,
        input[type="color"] {
            width: 100%;
            background: rgba(255, 255, 255, 0.05);
            border: 1px solid var(--border);
            color: var(--text);
            border-radius: 8px;
            padding: 6px 8px;
        }

        /* Skip indicator */
        #skip-indicator {
            position: absolute;
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            background: rgba(0, 0, 0, 0.7);
            color: var(--text);
            padding: 12px 20px;
            border-radius: 10px;
            font-weight: 600;
            font-size: 16px;
            pointer-events: none;
            opacity: 0;
            transition: opacity 0.2s ease;
            z-index: 10;
        }

        #skip-indicator.visible {
            opacity: 1;
        }

        /* Keyboard shortcuts */
        kbd {
            display: inline-block;
            padding: 2px 5px;
            font-size: 10px;
            font-family: ui-monospace, monospace;
            background: rgba(255, 255, 255, 0.1);
            border-radius: 3px;
            margin-left: 4px;
        }

        #log {
            font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace;
            font-size: 12px;
            white-space: pre-wrap;
            background: rgba(255, 255, 255, 0.04);
            border: 1px solid var(--border);
            border-radius: 10px;
            padding: 10px;
            color: var(--muted);
            display: none;
        }
    </style>
</head>
<body>
    <div id="chrome">
        <div id="header">
            <div id="title">{{TITLE}}</div>
        </div>
        <div id="player-wrapper">
            <div id="top-controls">
                <button id="cc-toggle" class="ctrl-btn" type="button" aria-pressed="false">CC</button>
                <button id="settings-toggle" class="ctrl-btn" type="button" aria-label="Subtitle settings">
                    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <circle cx="12" cy="12" r="3"></circle>
                        <path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"></path>
                    </svg>
                </button>
            </div>
            <div id="settings-panel" role="dialog" aria-label="Subtitle settings" data-open="false">
                <h3>Subtitles</h3>
                <div class="setting">
                    <div class="setting-row">
                        <label for="font-size">Font size</label>
                        <span id="font-size-value" class="value"></span>
                    </div>
                    <input id="font-size" type="range" min="14" max="38" step="1" />
                </div>
                <div class="setting">
                    <label for="font-family">Font family</label>
                    <select id="font-family">
                        <option value="Segoe UI">Segoe UI</option>
                        <option value="Arial">Arial</option>
                        <option value="Verdana">Verdana</option>
                        <option value="Tahoma">Tahoma</option>
                        <option value="Calibri">Calibri</option>
                    </select>
                </div>
                <div class="setting">
                    <label for="font-color">Text color</label>
                    <input id="font-color" type="color" />
                </div>
                <div class="setting">
                    <div class="setting-row">
                        <label for="bg-opacity">Background opacity</label>
                        <span id="bg-opacity-value" class="value"></span>
                    </div>
                    <input id="bg-opacity" type="range" min="0" max="0.9" step="0.05" />
                </div>
            </div>
            <video id="video" playsinline></video>
            <div id="status">Loading stream...</div>
            <div id="skip-indicator"></div>
        </div>
        <div id="control-bar">
            <button class="icon-btn" id="play-pause" type="button" title="Play/Pause (Space)">
                <svg id="play-icon" viewBox="0 0 24 24" fill="currentColor"><polygon points="5,3 19,12 5,21"></polygon></svg>
                <svg id="pause-icon" viewBox="0 0 24 24" fill="currentColor" style="display:none"><rect x="6" y="4" width="4" height="16"></rect><rect x="14" y="4" width="4" height="16"></rect></svg>
            </button>
            <button class="icon-btn" id="skip-back" type="button" title="Back 10s (←)">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 5V1L7 6l5 5V7c3.31 0 6 2.69 6 6s-2.69 6-6 6-6-2.69-6-6H4c0 4.42 3.58 8 8 8s8-3.58 8-8-3.58-8-8-8z"/><text x="12" y="14" text-anchor="middle" font-size="7" fill="currentColor" stroke="none">10</text></svg>
            </button>
            <button class="icon-btn" id="skip-forward" type="button" title="Forward 10s (→)">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 5V1l5 5-5 5V7c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6h2c0 4.42-3.58 8-8 8s-8-3.58-8-8 3.58-8 8-8z"/><text x="12" y="14" text-anchor="middle" font-size="7" fill="currentColor" stroke="none">10</text></svg>
            </button>
            <div id="volume-container">
                <button class="icon-btn" id="mute-toggle" type="button" title="Mute (M)">
                    <svg id="volume-icon" viewBox="0 0 24 24" fill="currentColor"><path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.06c1.48-.74 2.5-2.26 2.5-4.03zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z"/></svg>
                    <svg id="muted-icon" viewBox="0 0 24 24" fill="currentColor" style="display:none"><path d="M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z"/></svg>
                </button>
                <input type="range" id="volume-slider" min="0" max="1" step="0.05" value="1" />
            </div>
            <div id="progress-container">
                <div id="buffer-bar"></div>
                <div id="progress-bar"></div>
            </div>
            <span id="time-display">0:00 / 0:00</span>
            <div class="dropdown" id="speed-dropdown">
                <button class="icon-btn" id="speed-btn" type="button">1x</button>
                <div class="dropdown-menu">
                    <button class="dropdown-item" data-speed="0.5">0.5x</button>
                    <button class="dropdown-item" data-speed="0.75">0.75x</button>
                    <button class="dropdown-item active" data-speed="1">1x</button>
                    <button class="dropdown-item" data-speed="1.25">1.25x</button>
                    <button class="dropdown-item" data-speed="1.5">1.5x</button>
                    <button class="dropdown-item" data-speed="2">2x</button>
                </div>
            </div>
            <button class="icon-btn" id="fullscreen-btn" type="button" title="Fullscreen (F)">
                <svg id="expand-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3m0 18h3a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2h3"/></svg>
                <svg id="shrink-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="display:none"><path d="M4 14h6v6m10-10h-6V4m0 6 7-7M3 21l7-7"/></svg>
            </button>
        </div>
        <div id="log"></div>
    </div>
    <script>
        // Flip to true to surface inline logs during debugging.
        const enableLogging = false;

        const source = {{URL_JSON}};
        const title = {{TITLE_JSON}};
        const video = document.getElementById("video");
        const statusEl = document.getElementById("status");
        const logEl = enableLogging ? document.getElementById("log") : null;
        const subtitleUrl = {{SUB_JSON}};
        const subtitleLabel = {{SUB_LABEL_JSON}};
        const ccToggle = document.getElementById("cc-toggle");
        const settingsToggle = document.getElementById("settings-toggle");
        const settingsPanel = document.getElementById("settings-panel");
        const subtitleStyleEl = document.getElementById("subtitle-style");
        const fontSizeInput = document.getElementById("font-size");
        const fontSizeValue = document.getElementById("font-size-value");
        const fontFamilySelect = document.getElementById("font-family");
        const fontColorInput = document.getElementById("font-color");
        const bgOpacityInput = document.getElementById("bg-opacity");
        const bgOpacityValue = document.getElementById("bg-opacity-value");
        
        // New control elements
        const playPauseBtn = document.getElementById("play-pause");
        const playIcon = document.getElementById("play-icon");
        const pauseIcon = document.getElementById("pause-icon");
        const skipBackBtn = document.getElementById("skip-back");
        const skipForwardBtn = document.getElementById("skip-forward");
        const muteToggle = document.getElementById("mute-toggle");
        const volumeIcon = document.getElementById("volume-icon");
        const mutedIcon = document.getElementById("muted-icon");
        const volumeSlider = document.getElementById("volume-slider");
        const progressContainer = document.getElementById("progress-container");
        const progressBar = document.getElementById("progress-bar");
        const bufferBar = document.getElementById("buffer-bar");
        const timeDisplay = document.getElementById("time-display");
        const speedDropdown = document.getElementById("speed-dropdown");
        const speedBtn = document.getElementById("speed-btn");
        const fullscreenBtn = document.getElementById("fullscreen-btn");
        const expandIcon = document.getElementById("expand-icon");
        const shrinkIcon = document.getElementById("shrink-icon");
        const skipIndicator = document.getElementById("skip-indicator");
        
        let hlsInstance = null;
        let fallbackUsed = false;
        let subtitleTrackEl = null;
        let subtitlesEnabled = true;
        let controlsTimeout = null;
        let savedVolume = 1;
        const hasSubtitles = !!subtitleUrl;
        const subtitleDefaults = {
            fontSize: 22,
            fontFamily: "Segoe UI",
            color: "#ffffff",
            bgOpacity: 0.45
        };
        let subtitlePrefs = loadSubtitlePrefs();

        video.playsInline = true;
        video.volume = 1;

        function setStatus(text, isError = false) {
            statusEl.textContent = text || "";
            statusEl.style.opacity = text ? 1 : 0;
            statusEl.style.color = isError ? "var(--error)" : "var(--muted)";
        }

        function log(line) {
            if (!enableLogging) return;
            const stamp = new Date().toISOString().substring(11, 19);
            if (logEl) {
                logEl.textContent += `[${stamp}] ${line}\n`;
            }
            if (window.chrome?.webview?.postMessage) {
                try { window.chrome.webview.postMessage(line); } catch {}
            }
        }

        // Expose logging for the host (C#) to call.
        window.__log = log;
        if (enableLogging && window.chrome?.webview?.addEventListener) {
            window.chrome.webview.addEventListener("message", (e) => {
                log(e.data);
            });
        }

        // Quick HEAD to surface HTTP status even when hls.js dies early.
        (async () => {
            try {
                const res = await fetch(source, { method: "HEAD", mode: "cors" });
                log(`HEAD ${res.status} ${res.statusText} - ${source}`);
            } catch (err) {
                log(`HEAD failed: ${err?.message || err}`);
            }
        })();

        function logHlsResponse(data) {
            const code = data?.response?.code ?? data?.response?.status;
            const text = data?.response?.text ?? data?.response?.statusText;
            const url = data?.response?.url;
            if (code || url) {
                log(`HLS response: ${code || "?"} ${text || ""} ${url || ""}`.trim());
            }
        }

        function describeVideoError(err) {
            if (!err) return "Unknown video error";
            const codes = {
                1: "Aborted",
                2: "Network",
                3: "Decode",
                4: "Src not supported"
            };
            return `${codes[err.code] || "Error"} (${err.code})`;
        }

        function attachNativeHls() {
            if (!video.canPlayType("application/vnd.apple.mpegurl")) {
                return false;
            }

            video.src = source;
            video.addEventListener("loadedmetadata", () => video.play().catch(() => {}), { once: true });
            log("Using native HLS");
            return true;
        }

        function attachWithHlsJs() {
            if (!window.Hls || !Hls.isSupported()) {
                return false;
            }

            const hls = new Hls({
                lowLatencyMode: true,
                enableWorker: true,
                backBufferLength: 120,
                progressive: true,
                manifestLoadingTimeOut: 10000,
                manifestLoadingRetryDelay: 1000,
            });
            hlsInstance = hls;

            hls.on(Hls.Events.MANIFEST_PARSED, () => {
                log("Manifest parsed");
                setStatus("Starting playback...");
            });

            hls.on(Hls.Events.ERROR, (_event, data) => {
                const detail = `${data?.type || ""}/${data?.details || ""}`.trim();
                logHlsResponse(data);
                log(`HLS error: ${detail} (fatal=${data?.fatal})`);
                if (data?.fatal && !fallbackUsed && data?.details === "manifestLoadError") {
                    fallbackUsed = true;
                    setStatus("Retrying with native player...", true);
                    log("Retrying with native HLS after manifest load error.");
                    hls.destroy();
                    hlsInstance = null;
                    const fallbackAttached = attachNativeHls();
                    if (fallbackAttached) {
                        video.play().catch((err) => log(`Native play failed: ${err?.message || err}`));
                    }
                    return;
                }
                if (data?.fatal) {
                    setStatus(`Playback error (${detail}).`, true);
                }
            });

            hls.loadSource(source);
            hls.attachMedia(video);
            log("Using hls.js");
            attachSubtitle();
            return true;
        }

        function attachStandard() {
            video.src = source;
            log("Using direct src (non-HLS)");
            attachSubtitle();
            return true;
        }

        function attachSubtitle() {
            if (!subtitleUrl) {
                updateCcToggle(false, true);
                return;
            }
            const track = document.createElement("track");
            track.kind = "subtitles";
            track.src = subtitleUrl;
            track.default = true;
            track.label = subtitleLabel || "Subtitles";
            track.srclang = "en";
            video.appendChild(track);
            subtitleTrackEl = track;
            updateCcToggle(true, false);
        }

        function applySubtitleStyles() {
            const { fontSize, fontFamily, color, bgOpacity } = subtitlePrefs;
            const clampedOpacity = Math.max(0, Math.min(0.9, Number(bgOpacity) || 0));
            const css = `
video::cue {
  color: ${color} !important;
  font-size: ${fontSize}px !important;
  font-family: "${fontFamily}", "Segoe UI", sans-serif !important;
  background: rgba(0, 0, 0, ${clampedOpacity}) !important;
  padding: 2px 6px;
  line-height: 1.4;
  text-shadow: 0 2px 6px rgba(0, 0, 0, 0.55);
}`;
            // Force browser to re-parse ::cue styles by recreating the style element
            const oldStyle = document.getElementById("subtitle-style");
            if (oldStyle) oldStyle.remove();
            const newStyle = document.createElement("style");
            newStyle.id = "subtitle-style";
            newStyle.textContent = css;
            document.head.appendChild(newStyle);

            // Toggle track mode to force cue re-render
            const tracks = video.textTracks;
            for (let i = 0; i < tracks.length; i++) {
                const wasShowing = tracks[i].mode === "showing";
                if (wasShowing) {
                    tracks[i].mode = "hidden";
                    setTimeout(() => { tracks[i].mode = "showing"; }, 0);
                }
            }

            updateSettingsUi();
            persistSubtitlePrefs();
        }

        function updateSettingsUi() {
            if (fontSizeInput) fontSizeInput.value = subtitlePrefs.fontSize;
            if (fontSizeValue) fontSizeValue.textContent = `${subtitlePrefs.fontSize}px`;
            if (fontFamilySelect) fontFamilySelect.value = subtitlePrefs.fontFamily;
            if (fontColorInput) fontColorInput.value = subtitlePrefs.color;
            if (bgOpacityInput) bgOpacityInput.value = subtitlePrefs.bgOpacity;
            if (bgOpacityValue) bgOpacityValue.textContent = `${Math.round(subtitlePrefs.bgOpacity * 100)}%`;
        }

        function loadSubtitlePrefs() {
            try {
                const raw = localStorage.getItem("koware.subtitle.prefs");
                if (raw) {
                    const parsed = JSON.parse(raw);
                    return { ...subtitleDefaults, ...parsed };
                }
            } catch {}
            return { ...subtitleDefaults };
        }

        function persistSubtitlePrefs() {
            try {
                localStorage.setItem("koware.subtitle.prefs", JSON.stringify(subtitlePrefs));
            } catch {}
        }

        function syncSubtitleToggle(on) {
            if (!subtitleTrackEl) return;
            const tracks = video.textTracks;
            for (let i = 0; i < tracks.length; i++) {
                tracks[i].mode = on ? "showing" : "disabled";
            }
            if (ccToggle) {
                ccToggle.textContent = on ? "CC On" : "CC Off";
                ccToggle.setAttribute("aria-pressed", on ? "true" : "false");
            }
        }

        function updateCcToggle(enabled, disabledState) {
            if (!ccToggle) return;
            if (!enabled) {
                ccToggle.textContent = "CC N/A";
                ccToggle.disabled = true;
                return;
            }
            ccToggle.disabled = !!disabledState;
            ccToggle.textContent = subtitlesEnabled ? "CC On" : "CC Off";
            ccToggle.setAttribute("aria-pressed", subtitlesEnabled ? "true" : "false");
            ccToggle.onclick = () => {
                subtitlesEnabled = !subtitlesEnabled;
                syncSubtitleToggle(subtitlesEnabled);
            };
        }

        function wireSettingsPanel() {
            if (!settingsToggle || !settingsPanel) return;
            const toggle = (state) => {
                const open = typeof state === "boolean" ? state : settingsPanel.dataset.open !== "true";
                settingsPanel.dataset.open = open ? "true" : "false";
                settingsToggle.setAttribute("aria-expanded", open ? "true" : "false");
            };

            settingsToggle.onclick = () => toggle();
            document.addEventListener("keydown", (e) => {
                if (e.key === "Escape") toggle(false);
            });

            fontSizeInput?.addEventListener("input", (e) => {
                subtitlePrefs.fontSize = Number(e.target.value) || subtitleDefaults.fontSize;
                applySubtitleStyles();
            });

            fontFamilySelect?.addEventListener("change", (e) => {
                subtitlePrefs.fontFamily = e.target.value || subtitleDefaults.fontFamily;
                applySubtitleStyles();
            });

            fontColorInput?.addEventListener("input", (e) => {
                subtitlePrefs.color = e.target.value || subtitleDefaults.color;
                applySubtitleStyles();
            });

            bgOpacityInput?.addEventListener("input", (e) => {
                const parsed = Number(e.target.value);
                subtitlePrefs.bgOpacity = Number.isFinite(parsed) ? parsed : subtitleDefaults.bgOpacity;
                applySubtitleStyles();
            });

            updateSettingsUi();
        }

        // ===== New Control Functions =====
        
        function formatTime(seconds) {
            if (!isFinite(seconds)) return "0:00";
            const mins = Math.floor(seconds / 60);
            const secs = Math.floor(seconds % 60);
            return `${mins}:${secs.toString().padStart(2, "0")}`;
        }

        function updatePlayPauseIcon() {
            if (video.paused) {
                playIcon.style.display = "block";
                pauseIcon.style.display = "none";
            } else {
                playIcon.style.display = "none";
                pauseIcon.style.display = "block";
            }
        }

        function updateVolumeIcon() {
            if (video.muted || video.volume === 0) {
                volumeIcon.style.display = "none";
                mutedIcon.style.display = "block";
            } else {
                volumeIcon.style.display = "block";
                mutedIcon.style.display = "none";
            }
        }

        function updateProgress() {
            if (video.duration) {
                const pct = (video.currentTime / video.duration) * 100;
                progressBar.style.width = `${pct}%`;
                timeDisplay.textContent = `${formatTime(video.currentTime)} / ${formatTime(video.duration)}`;
            }
        }

        function updateBuffer() {
            if (video.buffered.length > 0 && video.duration) {
                const bufferedEnd = video.buffered.end(video.buffered.length - 1);
                const pct = (bufferedEnd / video.duration) * 100;
                bufferBar.style.width = `${pct}%`;
            }
        }

        function showSkipIndicator(text) {
            skipIndicator.textContent = text;
            skipIndicator.classList.add("visible");
            setTimeout(() => skipIndicator.classList.remove("visible"), 600);
        }

        function skip(seconds) {
            video.currentTime = Math.max(0, Math.min(video.duration || 0, video.currentTime + seconds));
            showSkipIndicator(seconds > 0 ? `+${seconds}s` : `${seconds}s`);
        }

        function setSpeed(speed) {
            video.playbackRate = speed;
            speedBtn.textContent = speed === 1 ? "1x" : `${speed}x`;
            speedDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.classList.toggle("active", parseFloat(item.dataset.speed) === speed);
            });
            speedDropdown.classList.remove("open");
            try { localStorage.setItem("koware.player.speed", speed); } catch {}
        }

        function toggleFullscreen() {
            if (document.fullscreenElement) {
                document.exitFullscreen();
            } else {
                document.documentElement.requestFullscreen();
            }
        }

        function updateFullscreenIcon() {
            const isFs = !!document.fullscreenElement;
            document.body.classList.toggle("fullscreen", isFs);
            expandIcon.style.display = isFs ? "none" : "block";
            shrinkIcon.style.display = isFs ? "block" : "none";
        }

        function showControls() {
            document.body.classList.add("controls-visible");
            clearTimeout(controlsTimeout);
            controlsTimeout = setTimeout(() => {
                if (!video.paused) {
                    document.body.classList.remove("controls-visible");
                }
            }, 3000);
        }

        function savePosition() {
            if (video.duration && video.currentTime > 5) {
                try {
                    const key = `koware.player.pos.${btoa(source).slice(0, 32)}`;
                    localStorage.setItem(key, JSON.stringify({
                        time: video.currentTime,
                        duration: video.duration,
                        savedAt: Date.now()
                    }));
                } catch {}
            }
        }

        function restorePosition() {
            try {
                const key = `koware.player.pos.${btoa(source).slice(0, 32)}`;
                const raw = localStorage.getItem(key);
                if (raw) {
                    const { time, duration, savedAt } = JSON.parse(raw);
                    // Only restore if saved within last 7 days and not near the end
                    if (Date.now() - savedAt < 7 * 24 * 60 * 60 * 1000 && time < duration - 30) {
                        video.currentTime = time;
                        showSkipIndicator(`Resumed at ${formatTime(time)}`);
                    }
                }
            } catch {}
        }

        function wireControls() {
            // Play/Pause
            playPauseBtn.onclick = () => video.paused ? video.play() : video.pause();
            video.addEventListener("play", updatePlayPauseIcon);
            video.addEventListener("pause", updatePlayPauseIcon);
            video.addEventListener("click", () => video.paused ? video.play() : video.pause());

            // Skip buttons
            skipBackBtn.onclick = () => skip(-10);
            skipForwardBtn.onclick = () => skip(10);

            // Volume
            muteToggle.onclick = () => {
                if (video.muted || video.volume === 0) {
                    video.muted = false;
                    video.volume = savedVolume || 1;
                } else {
                    savedVolume = video.volume;
                    video.muted = true;
                }
                volumeSlider.value = video.muted ? 0 : video.volume;
                updateVolumeIcon();
            };
            volumeSlider.oninput = () => {
                video.volume = parseFloat(volumeSlider.value);
                video.muted = video.volume === 0;
                if (video.volume > 0) savedVolume = video.volume;
                updateVolumeIcon();
            };
            video.addEventListener("volumechange", () => {
                volumeSlider.value = video.muted ? 0 : video.volume;
                updateVolumeIcon();
            });

            // Progress bar
            video.addEventListener("timeupdate", updateProgress);
            video.addEventListener("progress", updateBuffer);
            video.addEventListener("loadedmetadata", () => {
                updateProgress();
                restorePosition();
            });
            progressContainer.onclick = (e) => {
                const rect = progressContainer.getBoundingClientRect();
                const pct = (e.clientX - rect.left) / rect.width;
                video.currentTime = pct * video.duration;
            };

            // Speed dropdown
            speedBtn.onclick = () => speedDropdown.classList.toggle("open");
            speedDropdown.querySelectorAll(".dropdown-item").forEach(item => {
                item.onclick = () => setSpeed(parseFloat(item.dataset.speed));
            });
            // Restore saved speed
            try {
                const savedSpeed = localStorage.getItem("koware.player.speed");
                if (savedSpeed) setSpeed(parseFloat(savedSpeed));
            } catch {}

            // Fullscreen
            fullscreenBtn.onclick = toggleFullscreen;
            document.addEventListener("fullscreenchange", updateFullscreenIcon);

            // Close dropdowns on outside click
            document.addEventListener("click", (e) => {
                if (!speedDropdown.contains(e.target)) speedDropdown.classList.remove("open");
            });

            // Show controls on mouse move
            document.addEventListener("mousemove", showControls);
            document.addEventListener("touchstart", showControls);

            // Keyboard shortcuts
            document.addEventListener("keydown", (e) => {
                if (e.target.tagName === "INPUT" || e.target.tagName === "SELECT") return;
                
                switch (e.key.toLowerCase()) {
                    case " ":
                    case "k":
                        e.preventDefault();
                        video.paused ? video.play() : video.pause();
                        break;
                    case "arrowleft":
                    case "j":
                        e.preventDefault();
                        skip(-10);
                        break;
                    case "arrowright":
                    case "l":
                        e.preventDefault();
                        skip(10);
                        break;
                    case "arrowup":
                        e.preventDefault();
                        video.volume = Math.min(1, video.volume + 0.1);
                        break;
                    case "arrowdown":
                        e.preventDefault();
                        video.volume = Math.max(0, video.volume - 0.1);
                        break;
                    case "m":
                        video.muted = !video.muted;
                        break;
                    case "f":
                        toggleFullscreen();
                        break;
                    case "0": case "1": case "2": case "3": case "4":
                    case "5": case "6": case "7": case "8": case "9":
                        if (video.duration) {
                            video.currentTime = (parseInt(e.key) / 10) * video.duration;
                        }
                        break;
                }
            });

            // Save position periodically and on unload
            setInterval(savePosition, 10000);
            window.addEventListener("beforeunload", savePosition);
        }

        (function start() {
            const lower = source.toLowerCase();
            const isHls = lower.includes(".m3u8") || lower.includes("master.m3u8");
            const attached = isHls
                ? (attachWithHlsJs() || attachNativeHls())
                : attachStandard();

            if (!attached) {
                setStatus("Your browser cannot play this stream.", true);
                log("Failed to attach any playback pipeline.");
                return;
            }

            wireSettingsPanel();
            wireControls();
            applySubtitleStyles();
            updatePlayPauseIcon();
            updateVolumeIcon();

            video.addEventListener("error", () => {
                const desc = describeVideoError(video.error);
                setStatus(`Failed to load video: ${desc}`, true);
                log(`Video error: ${desc}`);
            });
            video.addEventListener("playing", () => {
                setStatus("");
                log("Playing");
            });
            video.addEventListener("waiting", () => {
                setStatus("Buffering...");
                log("Buffering");
            });
            video.addEventListener("ended", () => {
                setStatus("Playback finished.");
                log("Ended");
                updatePlayPauseIcon();
            });
            video.addEventListener("stalled", () => {
                setStatus("Network stalled...", true);
                log("Stalled");
            });

            video.play().catch((err) => {
                setStatus("Press play to start.", false);
                log(`Autoplay blocked or failed: ${err?.message || err}`);
                updatePlayPauseIcon();
            });
        })();
    </script>
</body>
</html>
""";

        return template
            .Replace("{{TITLE}}", encodedTitle)
            .Replace("{{URL_JSON}}", urlJson)
            .Replace("{{TITLE_JSON}}", titleJson)
            .Replace("{{SUB_JSON}}", subtitleJson)
            .Replace("{{SUB_LABEL_JSON}}", subtitleLabelJson);
    }
}
