const pages = {
    overview: ["Overview", "System health, connected devices and recent activity."],
    ai: ["AI & Ollama", "Manage the local model and conversation limits."],
    voice: ["Voice", "Configure Whisper and Piper, then test each service."],
    vision: ["Vision", "Inspect still-image inference and configure visual context."],
    personality: ["Personality", "Edit the profile used for new robot replies."],
    memory: ["Memory", "Manage records stored in the server’s SQLite database."],
    robots: ["Robots", "Monitor active connections and confirm emergency stops."],
    logs: ["Logs", "Search recent diagnostic events from this server process."],
    settings: ["Settings", "Inspect saved overrides, effective values and access mode."]
};
const configFields = {
    "Ollama:BaseUrl": ["Ollama server URL", "url"],
    "Ollama:Model": ["Selected model"],
    "Ollama:TimeoutSeconds": ["Request timeout (seconds)", "number", 1, 600],
    "Ollama:Temperature": ["Temperature", "number", 0, 2, "any"],
    "Ollama:MaxResponseTokens": ["Maximum response tokens", "number", 1, 8192],
    "Conversation:MaxHistoryMessages": ["Conversation history messages", "number", 0, 40, 2],
    "Speech:STT:Language": ["Whisper language (blank for auto)"],
    "Speech:STT:ModelPath": ["Whisper model directory"],
    "Speech:STT:TimeoutSeconds": ["Whisper timeout (seconds)", "number", 1, 600],
    "Speech:TTS:VoiceModelPath": ["Piper voice model"],
    "Speech:TTS:TimeoutSeconds": ["Piper timeout (seconds)", "number", 1, 600],
    "Vision:ContextLifetimeSeconds": ["Visual context lifetime (seconds)", "number", 1, 600]
};
const profileFields = {
    name: ["Robot name", 80], identity: ["Identity", 200, true], description: ["Description", 300],
    language: ["Language", 80, true], communicationStyle: ["Communication style", 200, true],
    humorLevel: ["Humor level", 100], verbosity: ["Verbosity", 100],
    defaultExpression: ["Default expression", ["idle", "happy", "sad", "thinking", "error"]],
    systemInstructions: ["Custom system instructions", 600]
};
const categories = ["personal", "preferences", "people_pets", "projects", "routines", "other"];
const view = document.querySelector("#view");
const connection = document.querySelector("#connection");
let current;
let controller;
let dirty = false;
let dispose = () => {};

function el(tag, className, ...children) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    node.append(...children);
    return node;
}
function badge(text, tone) {
    const node = el("span", "badge", text);
    if (tone) node.dataset.tone = tone;
    return node;
}
function button(text, action, className = "") {
    const node = el("button", className, text);
    node.type = "button";
    node.addEventListener("click", action);
    return node;
}
function note() {
    const node = el("p", "notice");
    node.setAttribute("role", "status");
    return node;
}
function feedback(node, text, error = false) {
    node.textContent = text;
    node.classList.toggle("error", error);
}
function link(target, label) {
    const node = el("a", "quick-link", label, el("span", "", "↗"));
    node.href = "#" + target;
    return node;
}
function panel(title, ...children) {
    return el("section", "panel stack", el("h2", "", title), ...children);
}
function metrics(container, entries) {
    container.replaceChildren(...entries.map(([label, value]) => el("div", "metric",
        el("span", "metric-label", label), el("span", "metric-value", value ?? "—"))));
}
function time(value) {
    if (!value) return "—";
    const node = el("time", "", new Date(value).toLocaleString());
    node.dateTime = value;
    return node;
}
function duration(seconds) {
    if (seconds < 60) return seconds + " s";
    if (seconds < 3600) return Math.floor(seconds / 60) + " min " + seconds % 60 + " s";
    return Math.floor(seconds / 3600) + " h " + Math.floor(seconds % 3600 / 60) + " min";
}
function table(headers, rows, empty = "No records.") {
    if (!rows.length) return el("p", "empty", empty);
    const cells = headers.map(label => {
        const cell = el("th", "", label);
        cell.scope = "col";
        return cell;
    });
    return el("div", "table-wrap", el("table", "", el("thead", "", el("tr", "", ...cells)),
        el("tbody", "", ...rows.map(row => el("tr", "", ...row.map(value => el("td", "", value ?? "—")))))));
}
async function api(path, signal, options = {}) {
    const { body, audio, headers, ...init } = options;
    const response = await fetch("/api/" + path, {
        ...init, signal, cache: "no-store", redirect: "error",
        headers: { ...(body === undefined ? {} : { "Content-Type": "application/json" }), ...headers },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        const detail = error.title || error.message || error.code || "Request failed";
        throw new Error(detail + (error.fields?.length ? " " + error.fields.join(", ") : "") + " (" + response.status + ")");
    }
    if (response.status === 204) return;
    return audio ? response.blob() : response.json();
}
async function perform(control, output, signal, action) {
    control.disabled = true;
    feedback(output, "Working…");
    try { await action(); }
    catch (error) { if (!signal.aborted) feedback(output, error.message, true); }
    finally { control.disabled = false; }
}
function poll(signal, interval, action, onError) {
    let timer;
    const tick = async () => {
        try { if (!document.hidden) await action(); }
        catch (error) { if (!signal.aborted) onError(error); }
        finally { if (!signal.aborted) timer = setTimeout(tick, interval); }
    };
    signal.addEventListener("abort", () => clearTimeout(timer), { once: true });
    void tick();
}
function serverState(text, tone) {
    connection.textContent = text;
    connection.dataset.tone = tone;
}
async function systemStatus(signal) {
    const status = await api("system/status", signal);
    if (!signal.aborted) serverState("Server online", "good");
    return status;
}
function ollamaHealth(status) {
    if (!status.ollamaConnected) return badge("Unreachable", "bad");
    return status.available ? badge("Model available", "good") : badge("Model unavailable", "warn");
}
function speechHealth(check) {
    if (!check) return badge("Awaiting check", "warn");
    return check.status === "ready" ? badge("Ready", "good") : badge("Check failed", "bad");
}
function overview(signal) {
    const grid = el("div", "grid");
    const events = el("div", "", el("p", "empty", "Loading events…"));
    const statusNote = note();
    view.append(el("div", "stack", grid, statusNote, el("div", "split",
        panel("Recent system events", events),
        panel("Quick configuration", link("ai", "AI & Ollama"), link("voice", "Voice services"),
            link("vision", "Vision diagnostics"), link("robots", "Robot monitor")))));
    poll(signal, 5000, async () => {
        const status = await systemStatus(signal);
        const d = status.diagnostics;
        metrics(grid, [
            ["SERVER", badge("Online", "good")], ["UPTIME", duration(d.uptimeSeconds)],
            ["OLLAMA", ollamaHealth(status.ollama)], ["ACTIVE MODEL", status.ollama.model],
            ["WHISPER STT", speechHealth(d.whisper)], ["PIPER TTS", speechHealth(d.piper)],
            ["CONNECTED ROBOTS", String(status.connectedRobots)], ["ACTIVE SIMULATORS", String(status.connectedSimulators)],
            ["LAST AI REQUEST", time(d.lastAiRequest)], ["LAST VISION ANALYSIS", time(d.lastVisionAnalysis?.at)]
        ]);
        const entries = await api("system/logs", signal);
        events.replaceChildren(entries.length ? el("ul", "row-list", ...entries.slice(0, 5).map(entry =>
            el("li", "", time(entry.timestamp), el("div", "", entry.message)))) : el("p", "empty", "No events recorded."));
        feedback(statusNote, "");
    }, error => {
        serverState("Update failed", "bad");
        feedback(statusNote, "Status refresh failed. Displayed values may be stale. " + error.message, true);
    });
}
function field(label, input, hint) {
    return el("label", "field", el("span", "", label), input, ...(hint ? [el("small", "", hint)] : []));
}
function select(values) {
    return el("select", "", ...values.map(value => {
        const option = el("option", "", value || "All categories");
        option.value = value;
        return option;
    }));
}
function settingsEditor(keys, config, signal) {
    const form = el("form", "stack");
    const fields = el("div", "grid");
    const status = note();
    const restart = el("p", "description");
    const inputs = new Map();
    for (const key of keys) {
        const [label, type = "text", min, max, step] = configFields[key];
        const input = el("input");
        input.type = type;
        input.maxLength = 512;
        input.autocomplete = "off";
        if (min !== undefined) { input.min = min; input.max = max; input.step = step ?? 1; }
        const toggle = el("input");
        toggle.type = "checkbox";
        const hint = el("small", "muted");
        toggle.addEventListener("change", () => {
            input.disabled = !toggle.checked;
            if (toggle.checked) input.focus();
        });
        inputs.set(key, { input, toggle, hint });
        fields.append(el("div", "field", field(label, input), hint, el("label", "override", toggle, "Save override")));
    }
    function render() {
        restart.textContent = config.restartRequired
            ? "Saved changes are pending. Restart HomeRobot.Server to load them."
            : "Saved changes take effect after a server restart. Environment and command-line overrides take precedence.";
        restart.classList.toggle("warn", config.restartRequired);
        for (const [key, { input, toggle, hint }] of inputs) {
            toggle.checked = Object.hasOwn(config.saved, key);
            input.value = toggle.checked ? config.saved[key] : config.effective[key] ?? "";
            input.disabled = !toggle.checked;
            input.required = key !== "Speech:STT:Language";
            hint.textContent = "Effective: " + (config.effective[key] === "" ? "automatic" :
                config.effective[key] ?? "default or hidden") + (config.overridden.includes(key) ? " · deployment override" : "");
        }
    }
    render();
    const save = el("button", "primary", "Save settings");
    save.type = "submit";
    form.append(restart, fields, el("div", "controls", save), status);
    form.addEventListener("submit", event => {
        event.preventDefault();
        void perform(save, status, signal, async () => {
            const values = {};
            for (const [key, { input, toggle }] of inputs) {
                const next = toggle.checked ? input.value.trim() : null;
                if (next !== (config.saved[key] ?? null)) values[key] = next;
            }
            config = await api("system/config", signal, { method: "PATCH", body: values });
            render();
            feedback(status, config.restartRequired ? "Saved. Restart required." : "Settings saved.");
        });
    });
    return form;
}
async function servicePage(page, signal) {
    const keys = Object.keys(configFields).filter(key =>
        page === "settings" || page === "ai" && /^(Ollama|Conversation):/.test(key) ||
        page === "voice" && key.startsWith("Speech:") || page === "vision" && key.startsWith("Vision:"));
    const config = await api("system/config", signal);
    if (signal.aborted) return;
    serverState("Server online", "good");
    if (page === "settings") {
        view.append(el("div", "stack", panel("Access", el("p", "description", config.lanEnabled
            ? "LAN access is enabled in configuration. Remote requests require HTTPS and administrator credentials."
            : "Local access only. LAN access requires explicit server configuration.")),
            panel("Server configuration", settingsEditor(keys, config, signal))));
        return;
    }
    const grid = el("div", "grid");
    const refreshNote = note();
    const tools = el("div", "controls");
    const result = note();
    const details = panel("Service diagnostics", grid, tools, result, refreshNote);
    let audioUrl;
    if (page === "ai") {
        const test = button("Test effective connection", () => perform(test, result, signal, async () => {
            const status = await api("system/test-ollama", signal, { method: "POST" });
            feedback(result, !status.ollamaConnected ? "Could not connect to Ollama." : status.available
                ? status.model + " is available." : "Ollama responded, but the selected model could not be verified.", !status.available);
        }));
        tools.append(test);
    } else if (page === "voice") {
        const audio = el("audio");
        audio.controls = true;
        audio.hidden = true;
        const stt = button("Check Whisper", () => perform(stt, result, signal, async () => {
            await api("system/test-stt", signal, { method: "POST" });
            feedback(result, "Whisper processed a silent test sample.");
        }));
        const tts = button("Generate Czech sample", () => perform(tts, result, signal, async () => {
            const wave = await api("voice/synthesize", signal, {
                method: "POST", body: { text: "Ahoj, tady domácí robot. Zkouším český hlas." }, audio: true
            });
            if (signal.aborted) return;
            if (audioUrl) URL.revokeObjectURL(audioUrl);
            audio.src = audioUrl = URL.createObjectURL(wave);
            audio.hidden = false;
            feedback(result, "Piper generated the sample. Use the player to listen.");
        }));
        tools.append(stt, tts, audio);
        dispose = () => {
            audio.pause();
            if (audioUrl) URL.revokeObjectURL(audioUrl);
        };
        details.append(el("p", "description",
            "Audio input is PCM WAV, up to 30 seconds and 6 MiB. Piper output format follows the installed voice."));
    } else {
        details.append(el("p", "description",
            "The simulator owns the camera. Auto Scan and change detection are managed in the macOS simulator and are unavailable through the server."));
    }
    view.append(el("div", "stack", details, panel("Configuration", settingsEditor(keys, config, signal))));
    poll(signal, 5000, async () => {
        const status = await systemStatus(signal);
        const d = status.diagnostics;
        if (page === "ai") metrics(grid, [["OLLAMA", ollamaHealth(status.ollama)], ["EFFECTIVE MODEL", status.ollama.model]]);
        if (page === "voice") metrics(grid, [
            ["WHISPER STT", speechHealth(d.whisper)], ["PIPER TTS", speechHealth(d.piper)],
            ["LAST WHISPER CHECK", time(d.whisper?.at)], ["LAST PIPER CHECK", time(d.piper?.at)],
            ...(d.whisper?.error ? [["WHISPER ERROR", d.whisper.error]] : []),
            ...(d.piper?.error ? [["PIPER ERROR", d.piper.error]] : [])
        ]);
        if (page === "vision") metrics(grid, [
            ["VISION BACKEND MODEL", status.ollama.model], ["OLLAMA MODEL STATUS", ollamaHealth(status.ollama)],
            ["LAST SUCCESSFUL ANALYSIS", time(d.lastVisionAnalysis?.at)],
            ["LAST INFERENCE", d.lastVisionAnalysis ? d.lastVisionAnalysis.inferenceMs + " ms" : "—"],
            ["ANALYSIS REQUESTS", String(d.visionRequests)], ["CAMERA SETTINGS", "Simulator only"]
        ]);
        feedback(refreshNote, "");
    }, error => {
        serverState("Update failed", "bad");
        feedback(refreshNote, error.message, true);
    });
}
async function personality(signal) {
    const profile = await api("personality", signal);
    if (signal.aborted) return;
    serverState("Server online", "good");
    const form = el("form", "stack");
    const inputs = {};
    const fields = el("div", "grid");
    for (const [key, [label, limit, required]] of Object.entries(profileFields)) {
        const input = Array.isArray(limit) ? select(limit) : el(limit >= 300 ? "textarea" : "input");
        if (!Array.isArray(limit)) {
            input.autocomplete = "off";
            input.maxLength = limit;
            input.required = Boolean(required);
        }
        input.value = profile[key];
        inputs[key] = input;
        fields.append(field(label, input));
    }
    const output = note();
    const save = el("button", "primary", "Save personality");
    save.type = "submit";
    const restore = button("Discard changes", () => {
        for (const [key, input] of Object.entries(inputs)) input.value = profile[key];
        dirty = false;
        feedback(output, "Changes discarded.");
    });
    form.append(fields, el("div", "controls", save, restore), output);
    form.addEventListener("input", () => {
        dirty = Object.entries(inputs).some(([key, input]) => input.value !== profile[key]);
    });
    form.addEventListener("submit", event => {
        event.preventDefault();
        const values = Object.fromEntries(Object.entries(inputs).map(([key, input]) => [key, input.value]));
        if (Object.values(values).some(value => /[\x00-\x1f\x7f-\x9f]/.test(value))) {
            feedback(output, "Use single-line values without control characters.", true);
            return;
        }
        void perform(save, output, signal, async () => {
            Object.assign(profile, await api("personality", signal, { method: "PUT", body: values }));
            if (!signal.aborted) {
                dirty = Object.entries(inputs).some(([key, input]) => input.value !== profile[key]);
                feedback(output, dirty ? "Saved. New edits are still unsaved." : "Personality saved.");
            }
        });
    });
    view.append(panel("Robot profile", form));
}
function memory(signal) {
    const search = el("input");
    search.type = "search";
    search.placeholder = "Search content";
    search.setAttribute("aria-label", "Search memory content");
    const filter = select(["", ...categories]);
    filter.setAttribute("aria-label", "Filter by category");
    const list = el("div", "", el("p", "empty", "Loading memories…"));
    const output = note();
    const form = el("form", "stack");
    const content = el("textarea");
    content.required = true;
    content.autocomplete = "off";
    content.maxLength = 500;
    const category = select(categories);
    const editTitle = el("h2", "", "New memory");
    const save = el("button", "primary", "Create memory");
    save.type = "submit";
    let selected;
    let entries = [];
    let latest = 0;

    function reset(entry) {
        selected = entry?.id;
        editTitle.textContent = selected ? "Edit memory" : "New memory";
        save.textContent = selected ? "Save memory" : "Create memory";
        content.value = entry?.content ?? "";
        category.value = entry?.category ?? "other";
        cancel.hidden = !selected;
    }
    const cancel = button("Cancel edit", () => reset());
    cancel.hidden = true;
    const formNote = note();
    form.append(el("div", "grid", field("Content", content), field("Category", category)),
        el("div", "controls", save, cancel), formNote);
    function render() {
        const query = search.value.trim().toLocaleLowerCase();
        const matches = entries.filter(entry => entry.content.toLocaleLowerCase().includes(query));
        list.replaceChildren(table(["Content", "Category", "Created", "Updated", "Actions"], matches.map(entry => [
            entry.content, entry.category, time(entry.createdAt), time(entry.updatedAt),
            el("div", "controls", button("Edit", () => {
                reset(entry);
                feedback(formNote, "");
                content.focus();
            }), button("Delete", event => {
                if (!window.confirm("Delete this memory?")) return;
                void perform(event.currentTarget, output, signal, async () => {
                    await api("memory/" + entry.id, signal, { method: "DELETE" });
                    if (selected === entry.id) reset();
                    await load();
                    feedback(output, "Memory deleted.");
                });
            }))
        ]), "No matching memories."));
    }
    async function load() {
        const generation = ++latest;
        const records = await api("memory" + (filter.value ? "?category=" + encodeURIComponent(filter.value) : ""), signal);
        if (generation !== latest || signal.aborted) return;
        entries = records;
        serverState("Server online", "good");
        render();
    }
    function reload() {
        feedback(output, "");
        void load().catch(error => { if (!signal.aborted) feedback(output, error.message, true); });
    }
    const purge = button("Delete all memories", () => {
        if (window.prompt("This deletes every category permanently. Type DELETE ALL to confirm.") !== "DELETE ALL") return;
        void perform(purge, output, signal, async () => {
            await api("memory", signal, { method: "DELETE", headers: { "X-Confirm-Memory-Deletion": "delete-all" } });
            reset();
            await load();
            feedback(output, "All memories deleted.");
        });
    }, "danger");
    form.addEventListener("submit", event => {
        event.preventDefault();
        const editing = selected;
        const value = content.value.trim();
        if (!value || /[\x00-\x1f\x7f-\x9f]/.test(value)) {
            feedback(formNote, "Enter non-empty, single-line content.", true);
            return;
        }
        void perform(save, formNote, signal, async () => {
            await api(editing ? "memory/" + editing : "memory", signal,
                { method: editing ? "PUT" : "POST", body: { content: value, category: category.value } });
            reset();
            await load();
            feedback(formNote, editing ? "Memory updated." : "Memory created.");
        });
    });
    search.addEventListener("input", render);
    filter.addEventListener("change", reload);
    view.append(el("div", "stack", el("section", "panel stack", editTitle, form),
        panel("Stored memories", el("div", "controls", search, filter, button("Refresh", reload), purge), output, list)));
    reload();
}
function robots(signal) {
    const grid = el("div", "stack");
    const history = el("div", "", el("p", "empty", "Waiting for robot data…"));
    const statusNote = note();
    const cards = new Map();
    const selector = el("select");
    selector.setAttribute("aria-label", "Robot command history");
    selector.disabled = true;
    let selected = "";
    let seen = [];
    let historyRequest = 0;
    const robotPath = id => "robots/" + encodeURIComponent(id);
    function createCard(robot) {
        const details = el("div", "grid");
        const status = note();
        const stop = button("Emergency Stop", () => perform(stop, status, signal, async () => {
            const path = robotPath(robot.robotId);
            let command = await api(path + "/stop", signal, { method: "POST" });
            feedback(status, "Stop submitted. Waiting for robot confirmation…");
            const deadline = Date.now() + 30000;
            while (["pending", "accepted", "executing"].includes(command.status) && Date.now() < deadline) {
                await new Promise(resolve => setTimeout(resolve, 400));
                if (signal.aborted) return;
                command = await api(path + "/commands/" + encodeURIComponent(command.commandId), signal);
                feedback(status, "Stop status: " + command.status + "…");
            }
            feedback(status, command.status === "completed" ? "Emergency stop confirmed by robot."
                : "Stop was not confirmed (" + command.status + ").", command.status !== "completed");
        }), "danger");
        const testSpeaker = button("Test Speaker", () => perform(testSpeaker, status, signal, async () => {
            const result = await api("audio/" + encodeURIComponent(robot.robotId) + "/test", signal, { method: "POST" });
            feedback(status, "Speech submitted: " + result.playbackId + ". Watch playback status for completion.");
        }));
        testSpeaker.hidden = robot.deviceType !== "esp32" ||
            !["audio_output", "tts_playback", "audio_stop"].every(value => robot.capabilities.includes(value));
        const availability = badge("Connected", "good");
        const title = el("div", "section-title", el("h2", "", robot.name), availability);
        const node = el("section", "panel stack", title, details, el("div", "controls", stop, testSpeaker), status);
        return { node, details, stop, testSpeaker, availability, connected: true };
    }
    async function loadHistory() {
        const generation = ++historyRequest;
        if (!selected) {
            history.replaceChildren(el("p", "empty", "Connect a robot to inspect its command history."));
            return;
        }
        const commands = await api(robotPath(selected) + "/commands", signal);
        if (generation !== historyRequest || signal.aborted) return;
        history.replaceChildren(table(["Command", "Status", "Created", "Updated", "Detail"], commands.map(command => [
            command.command, badge(command.status, command.status === "completed" ? "good" :
                ["failed", "cancelled", "timeout", "disconnected"].includes(command.status) ? "bad" : "warn"),
            time(command.createdAt), time(command.updatedAt), command.message
        ]), "No commands recorded for this robot."));
    }
    selector.addEventListener("change", () => {
        selected = selector.value;
        history.replaceChildren(el("p", "empty", "Loading command history…"));
        void loadHistory().catch(error => { if (!signal.aborted) feedback(statusNote, error.message, true); });
    });
    view.append(el("div", "stack", statusNote, grid, panel("Command history", selector, history)));
    poll(signal, 1500, async () => {
        const devices = await api("robots", signal);
        if (signal.aborted) return;
        serverState("Server online", "good");
        const active = new Set(devices.map(robot => robot.robotId));
        for (const [id, card] of cards) {
            if (!active.has(id) && card.connected) {
                card.connected = false;
                card.availability.textContent = "Disconnected";
                card.availability.dataset.tone = "bad";
                card.stop.hidden = true;
                card.testSpeaker.disabled = true;
            }
        }
        for (const robot of devices) {
            if (!cards.has(robot.robotId)) cards.set(robot.robotId, createCard(robot));
            const card = cards.get(robot.robotId);
            card.connected = true;
            card.availability.textContent = "Connected";
            card.availability.dataset.tone = "good";
            card.stop.hidden = false;
            card.testSpeaker.disabled = false;
            metrics(card.details, [
                ["ROBOT ID", robot.robotId], ["CLIENT TYPE", robot.deviceType],
                ["LAST HEARTBEAT", time(robot.lastHeartbeat)], ["LAST TELEMETRY", time(robot.lastTelemetry)],
                ["AUDIO CAPABILITY", robot.capabilities.includes("tts_playback") ? "TTS playback" : "Unavailable"],
                ["PLAYBACK STATE / RESULT", robot.audio?.status ?? "idle"],
                ["PLAYBACK ID", robot.audio?.playbackId], ["LAST AUDIO ERROR", robot.audio?.error],
                ["CURRENT STATE", robot.telemetry?.state], ["LAST EXECUTED COMMAND", robot.lastExecutedCommand?.command],
                ["POSITION (X, Y)", robot.telemetry ? robot.telemetry.position.x.toFixed(1) + ", " +
                    robot.telemetry.position.y.toFixed(1) + " cm" : null],
                ["HEADING", robot.telemetry ? robot.telemetry.position.heading.toFixed(1) + "°" : null],
                ["SPEED", robot.telemetry ? robot.telemetry.movement.speed.toFixed(1) + "%" : null],
                ["REPORTED BATTERY", robot.telemetry ? robot.telemetry.batteryPercent + "%" : null]
            ]);
        }
        const ids = [...cards.keys()];
        if (ids.length !== seen.length || !grid.firstChild) {
            seen = ids;
            grid.replaceChildren(...(ids.length ? ids.map(id => cards.get(id).node) :
                [panel("Connected clients", el("p", "empty", "No robots or simulators connected."))]));
            selected ||= ids[0] ?? "";
            const options = select(ids);
            selector.replaceChildren(...options.children);
            selector.value = selected;
            selector.disabled = !ids.length;
        }
        await loadHistory();
        feedback(statusNote, "");
    }, error => {
        serverState("Update failed", "bad");
        feedback(statusNote, "Robot data may be stale. " + error.message, true);
    });
}
function logs(signal) {
    const query = el("input");
    query.type = "search";
    query.placeholder = "Search message or component";
    query.maxLength = 120;
    query.setAttribute("aria-label", "Search logs");
    const severity = select(["Information", "Warning", "Error", "Critical"]);
    severity.setAttribute("aria-label", "Minimum severity");
    const auto = el("input");
    auto.type = "checkbox";
    const output = note();
    const list = el("div", "", el("p", "empty", "Loading logs…"));
    let request = 0;
    async function load() {
        const generation = ++request;
        const params = new URLSearchParams({ query: query.value.trim(), minimum: severity.value });
        const entries = await api("system/logs?" + params, signal);
        if (generation !== request || signal.aborted) return;
        serverState("Server online", "good");
        list.replaceChildren(table(["Timestamp", "Severity", "Component", "Message"], entries.map(entry => [
            time(entry.timestamp), entry.severity, entry.component, entry.message
        ]), "No matching events."));
        feedback(output, "");
    }
    function onError(error) { if (!signal.aborted) feedback(output, error.message, true); }
    function refresh() { void load().catch(onError); }
    query.addEventListener("search", refresh);
    query.addEventListener("keydown", event => { if (event.key === "Enter") refresh(); });
    severity.addEventListener("change", refresh);
    view.append(panel("Diagnostic log", el("p", "description", "The server keeps the latest 250 approved events in memory."),
        el("div", "controls", query, severity, button("Refresh", refresh), el("label", "override", auto, "Auto-refresh")),
        output, list));
    refresh();
    poll(signal, 4000, async () => { if (auto.checked) await load(); }, onError);
}

const renderers = { overview, personality, memory, robots, logs };
function route() {
    if (location.hash === "#main") return;
    const hash = location.hash.slice(1);
    const page = Object.hasOwn(pages, hash) ? hash : "overview";
    if (page === current) return;
    if (dirty && !window.confirm("Discard unsaved personality changes?")) {
        history.replaceState(null, "", "#" + current);
        return;
    }
    controller?.abort();
    dispose();
    dispose = () => {};
    dirty = false;
    current = page;
    controller = new AbortController();
    const signal = controller.signal;
    const [title, description] = pages[page];
    document.querySelector("#breadcrumb").textContent = title;
    for (const anchor of document.querySelectorAll("nav a")) {
        if (anchor.hash === "#" + page) anchor.setAttribute("aria-current", "page");
        else anchor.removeAttribute("aria-current");
    }
    const heading = el("section", page === "overview" ? "heading hero" : "heading",
        el("p", "eyebrow", page === "overview" ? "Home Robot / Operations" : "Home Robot / Console"),
        el("h1", "", title), el("p", "", description));
    view.replaceChildren(heading);
    const render = renderers[page] ?? (() => servicePage(page, signal));
    void Promise.resolve(render(signal)).catch(error => {
        if (!signal.aborted) {
            serverState("Request failed", "bad");
            view.append(el("p", "error", error.message));
        }
    });
}
window.addEventListener("hashchange", route);
window.addEventListener("beforeunload", event => {
    if (dirty) {
        event.preventDefault();
        event.returnValue = "";
    }
});
route();
