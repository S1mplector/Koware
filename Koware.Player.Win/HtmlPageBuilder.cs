using System.Net;
using System.Text.Json;

namespace Koware.Player.Win;

internal static class HtmlPageBuilder
{
    public static string Build(PlayerArguments args)
    {
        var urlJson = JsonSerializer.Serialize(args.Url.ToString());
        var titleJson = JsonSerializer.Serialize(args.Title);
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
        :root {{
            color-scheme: dark;
            --bg: #0f172a;
            --panel: rgba(15, 23, 42, 0.75);
            --border: rgba(226, 232, 240, 0.1);
            --text: #e2e8f0;
            --muted: #94a3b8;
            --accent: #38bdf8;
            --error: #f97066;
        }}

        * {{
            box-sizing: border-box;
        }}

        body {{
            margin: 0;
            padding: 24px;
            background: radial-gradient(circle at 25% 25%, rgba(56, 189, 248, 0.1), transparent 30%),
                        radial-gradient(circle at 80% 20%, rgba(248, 113, 113, 0.07), transparent 25%),
                        var(--bg);
            color: var(--text);
            font-family: "Segoe UI", "Inter", system-ui, -apple-system, sans-serif;
            display: grid;
            place-items: center;
            min-height: 100vh;
        }}

        #chrome {{
            width: min(1100px, 100%);
            background: var(--panel);
            border: 1px solid var(--border);
            border-radius: 18px;
            box-shadow: 0 24px 70px rgba(0, 0, 0, 0.35), inset 0 0 0 1px rgba(255, 255, 255, 0.02);
            backdrop-filter: blur(20px);
            padding: 18px;
            display: grid;
            gap: 12px;
        }}

        #title {{
            font-weight: 700;
            letter-spacing: 0.02em;
            color: var(--text);
            opacity: 0.9;
            text-shadow: 0 2px 16px rgba(56, 189, 248, 0.25);
        }}

        #player-wrapper {{
            position: relative;
            overflow: hidden;
            border-radius: 14px;
            border: 1px solid var(--border);
        }}

        video {{
            width: 100%;
            height: 62vh;
            max-height: 720px;
            background: #0b1221;
        }}

        #status {{
            position: absolute;
            inset: 0;
            display: grid;
            place-items: center;
            font-weight: 600;
            color: var(--muted);
            pointer-events: none;
            text-shadow: 0 1px 8px rgba(0, 0, 0, 0.35);
            transition: opacity 0.25s ease;
        }}
    </style>
</head>
<body>
    <div id="chrome">
        <div id="title">{{TITLE}}</div>
        <div id="player-wrapper">
            <video id="video" controls autoplay playsinline></video>
            <div id="status">Loading stream…</div>
        </div>
    </div>
    <script>
        const source = {{URL_JSON}};
        const title = {{TITLE_JSON}};
        const video = document.getElementById("video");
        const statusEl = document.getElementById("status");

        video.playsInline = true;

        function setStatus(text, isError = false) {{
            statusEl.textContent = text || "";
            statusEl.style.opacity = text ? 1 : 0;
            statusEl.style.color = isError ? "var(--error)" : "var(--muted)";
        }}

        function attachNativeHls() {{
            if (!video.canPlayType("application/vnd.apple.mpegurl")) {{
                return false;
            }}

            video.src = source;
            video.addEventListener("loadedmetadata", () => video.play().catch(() => {{}}), {{ once: true }});
            return true;
        }}

        function attachWithHlsJs() {{
            if (!window.Hls || !Hls.isSupported()) {{
                return false;
            }}

            const hls = new Hls({{
                lowLatencyMode: true,
                enableWorker: true,
                backBufferLength: 120,
                progressive: true,
            }});

            hls.on(Hls.Events.ERROR, (_event, data) => {{
                if (data?.fatal) {{
                    setStatus("Playback error. Try another stream.", true);
                }}
            }});

            hls.loadSource(source);
            hls.attachMedia(video);
            return true;
        }}

        function attachStandard() {{
            video.src = source;
            return true;
        }}

        (function start() {{
            const lower = source.toLowerCase();
            const isHls = lower.includes(".m3u8") || lower.includes("master.m3u8");
            const attached = isHls
                ? (attachWithHlsJs() || attachNativeHls())
                : attachStandard();

            if (!attached) {{
                setStatus("Your browser cannot play this stream.", true);
                return;
            }}

            video.addEventListener("error", () => setStatus("Failed to load video.", true));
            video.addEventListener("playing", () => setStatus(""));
            video.addEventListener("waiting", () => setStatus("Buffering…"));
            video.addEventListener("ended", () => setStatus("Playback finished."));

            video.play().catch(() => setStatus("Press play to start.", false));
        }})();
    </script>
</body>
</html>
""";

        return template
            .Replace("{{TITLE}}", encodedTitle)
            .Replace("{{URL_JSON}}", urlJson)
            .Replace("{{TITLE_JSON}}", titleJson);
    }
}
