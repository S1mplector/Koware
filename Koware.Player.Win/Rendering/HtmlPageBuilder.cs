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
            padding: 24px;
            background:
                radial-gradient(circle at 25% 25%, rgba(56, 189, 248, 0.08), transparent 26%),
                radial-gradient(circle at 80% 20%, rgba(248, 113, 113, 0.06), transparent 22%),
                var(--bg);
            color: var(--text);
            font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
        }

        #chrome {
            width: min(1200px, 100%);
            background: linear-gradient(145deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.01)) var(--panel);
            border: 1px solid var(--border);
            border-radius: 16px;
            padding: 16px;
            display: grid;
            gap: 12px;
            box-shadow: 0 18px 45px rgba(0, 0, 0, 0.35);
        }

        #title {
            font-weight: 700;
            letter-spacing: 0.01em;
            color: var(--text);
            text-align: center;
        }

        #player-wrapper {
            position: relative;
            overflow: hidden;
            border-radius: 14px;
            border: 1px solid var(--border);
            background: radial-gradient(circle at 50% 35%, rgba(56, 189, 248, 0.08), rgba(15, 23, 42, 0.9));
            display: grid;
            place-items: center;
            aspect-ratio: 16 / 9;
            box-shadow: 0 12px 30px rgba(0, 0, 0, 0.3);
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

        #controls {
            position: absolute;
            top: 12px;
            right: 12px;
            display: flex;
            gap: 8px;
            z-index: 2;
        }

        #cc-toggle {
            border: 1px solid rgba(56, 189, 248, 0.35);
            background: rgba(56, 189, 248, 0.15);
            color: var(--text);
            border-radius: 999px;
            padding: 6px 12px;
            font-weight: 600;
            font-size: 12px;
            letter-spacing: 0.01em;
            cursor: pointer;
            transition: background 0.2s ease, border-color 0.2s ease, color 0.2s ease;
            box-shadow: 0 8px 20px rgba(0, 0, 0, 0.25);
        }

        #cc-toggle[aria-pressed="true"] {
            background: rgba(56, 189, 248, 0.15);
            border-color: rgba(56, 189, 248, 0.35);
        }

        #cc-toggle:disabled {
            opacity: 0.5;
            cursor: not-allowed;
            background: rgba(255, 255, 255, 0.06);
            border-color: rgba(226, 232, 240, 0.15);
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
            display: none; /* Set enableLogging to true below and remove this to show inline logs. */
        }
    </style>
</head>
<body>
    <div id="chrome">
        <div id="title">{{TITLE}}</div>
        <div id="player-wrapper">
            <div id="controls" aria-label="Player controls">
                <button id="cc-toggle" type="button" aria-pressed="false">CC</button>
            </div>
            <video id="video" controls autoplay playsinline></video>
            <div id="status">Loading stream...</div>
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
        let hlsInstance = null;
        let fallbackUsed = false;
        let subtitleTrackEl = null;
        let subtitlesEnabled = true;
        const hasSubtitles = !!subtitleUrl;

        video.playsInline = true;
        video.muted = true;

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
            });
            video.addEventListener("stalled", () => {
                setStatus("Network stalled...", true);
                log("Stalled");
            });

            video.play().catch((err) => {
                setStatus("Press play to start.", false);
                log(`Autoplay blocked or failed: ${err?.message || err}`);
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
