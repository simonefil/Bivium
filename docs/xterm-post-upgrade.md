# XTerm.NET 2.0.2: integration status

Partial implementation of the plan `2026-09-09 14_27 - plan - XTERM_NET_POST_UPGRADE.md`.

## Capabilities and paste

The session configuration keeps `Cols = 120`, `Rows = 30`, `Scrollback = Math.Max(256, HeadlessScrollbackRows)`, `TermName = xterm-256color` and `ConvertEol = true`.

| Option | Fork 2.0.2 default | Bivium |
| --- | --- | --- |
| SixelEnabled | true | false |
| KittyGraphicsEnabled | true | false |
| ITerm2ImagesEnabled | true | false |
| KittyKeyboardEnabled | true | true, used only after negotiation |
| KittyNotificationsEnabled | true | false |
| ClipboardWriteEnabled | true | false |
| ClipboardReadEnabled | false | false |
| PointerShapesEnabled | true | false |
| AllowPasteControls | false | false |

`SendPaste` uses `Terminal.Paste` under the session lock. The `DataReceived` event forwards the result to the PTY. Paste normalizes LF/CRLF to CR, preserves tab and Unicode, removes the other C0/C1 controls and DEL, and applies the negotiated bracketed paste. Normal input keeps its own path.

The DA, DECRQM, DECRQSS, XTGETTCAP and XTVERSION queries have reply tests on the `DataReceived` path. The tests also verify that the Kitty graphics, notification and pointer shape queries do not announce support; Kitty keyboard answers queries and preserves the negotiated stack.

## Circuit recovery

`connection.js` coordinates the Blazor modal, the workspace presence and the renderers. During detachment it suspends browser calls and invalidates pending terminal requests. Recovery first runs the workspace heartbeat, then visibility, resize, handoff with the final history page, and focus. Input restarts only after completion. Concurrent attempts share a Promise; a new disconnection invalidates the previous continuations.

The terminal component renews the runtime subscription when the lease generation changes. The coordination reuses the existing server rules for reacquisition and takeover.

A failed handoff, or one that takes longer than 10 seconds, causes a page reload. The modal keeps the reconnect/resume/reload fallback and retries every 5 seconds after the `failed` state. Server/proxy timeouts and keepalive are not modified and the PTY is not restarted by the recovery UI.

The events and the meaning of `hide`, `failed` and `rejected` follow the [ASP.NET Core 10 documentation](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/signalr?view=aspnetcore-10.0).

Verified with a deterministic JavaScript harness: call blocking during detachment, recovery order, no duplicated renderers and listeners, interruption during recovery, synchronous errors, timeout and reload on an unusable circuit or references.

Also verified in real headless Chrome on macOS, against local Bivium and the test proxy: WebSocket 1001 closure observed by the browser, new WebSocket, same page and same PTY PID, input/output working after recovery, a single renderer and no unhandled Promise. The first driver attempt confused focus and old output markers; the saved test uses unique markers, a dedicated PTY and DOM events explicitly addressed to the terminal.

Also verified the no longer recoverable circuit: the proxy prevented reconnections for 205 seconds, beyond the circuit retention. When the network was restored Bivium reloaded the page automatically; the page identifier changed, the PTY PID stayed identical and input/output restarted. No unhandled Promise and a single renderer. The fallback test reopens the terminal window from the new page to verify input, without creating another PTY.

Still to verify: hidden page, production reverse proxy and a second platform. Local recovery and fallback are verified; the original cause of the closure in production remains to be diagnosed.

## Shift+Enter and negotiated keyboard

The browser forwards `key`, `code`, Shift/Ctrl/Alt/Meta and the press/repeat/release events through `OnTerminalKey`. The runtime consults `KittyKeyboardActive` and uses `GenerateKittyKeyInput` only when the application has negotiated the mode. A null result means a suppressed event and does not trigger a second legacy attempt.

With `CSI > 1 u` active, Shift+Enter produces `CSI 13 ; 2 u`, while plain Enter preserves CR. With flags 11, repeat and release are also distinguishable. Popping the stack restores the previous mode; without negotiation Shift+Enter keeps the legacy CR behavior. Process names are not recognized and no specific fallback is forced for Codex or Claude. The contract follows the [Kitty keyboard specification](https://sw.kovidgoyal.net/kitty/keyboard-protocol/) and the public APIs of the fork.

The browser keeps the existing clipboard shortcuts and forwards the additional keys when the snapshot indicates Kitty is active. Handled presses are followed by the respective releases; blur releases the held keys. Suspension clears the press state and does not queue keys to be replayed at reconnect. Legacy releases produce no input and do not restart a terminated session.

Composed text stays on the `OnTerminalInput` path: the end-to-end test preserves `漢é👩‍💻🇮🇹` exactly, without a double commit. The library generator is not replaced to introduce new Unicode encodings.

Automated checks: negotiation/query, push/pop, normal/alternate, Shift+Enter and Enter, Meta, numpad physical code, F13, BMP text, repeat/release and suppression without fallback. Browser → Blazor → PTY verification succeeded with `terminal-keyboard-probe.py`, which checks the actual bytes and restores the terminal at the end.

Acceptance performed on September 9, 2026 with real Codex CLI 0.153.4 and Claude Code 2.1.265, in headless Chrome on macOS, through browser → Blazor → PTY. The CLIs were started in a dedicated temporary folder, with the local profiles and the user's authorization. The only prompt sent to each was `ciao come stai`.

Codex negotiated Kitty with `CSI > 7 u` and received the reply `CSI ? 7 u`; Claude enabled the alternate buffer with `CSI ? 1049 h` and Kitty with `CSI > 5 u` (also requesting modifyOtherKeys). In both CLIs Shift+Enter produced exactly `CSI 13 ; 2 u` and added a line in the composer without sending the message. Plain Enter produced CR and sent the prompt. A transparent intermediate PTY recorded the protocol sequences and the input bytes, without simulating the terminal replies. Local acceptance of Shift+Enter in the two CLIs is complete; SSH and multiplexers remain to be verified separately.

## Synchronized output — phase 5

Bivium subscribes to `SynchronizedOutputChanged` and keeps the last complete snapshot while DEC 2026 is active. The frame keeps cells, cursor, revision and history boundary together: concurrent snapshots and patches do not anticipate intermediate states; paging remains limited to the rows of the visible frame. Emulator, PTY replies, archiving, budget and export keep processing the current state.

The existing notification worker waits for the end of the block and publishes the update with the normal 16 ms aggregation window. The timeout confirmed by the user is 1 second, measured with a monotonic clock: once the deadline expires, updates restart even if no other output arrives. The interrupted block stays ignored until a subsequent off/on transition, without modifying the negotiated state in XTerm.NET.

Resize (even with unchanged geometry), handoff/reconnect and PTY exit interrupt the wait. The handoff immediately returns the current state; notifications use the existing worker. Detach keeps processing and archive active; removing the session removes the retained frame and terminates the worker. If trimming removes rows referenced by the frame, Bivium resumes publishing so as not to keep obsolete indexes.

The 13 automated cases cover normal/alternate, output in multiple chunks, timeout without further bytes, resume after timeout, new negotiation, resize, handoff, exit, detach, dispose, paging, budget, consecutive blocks and reset. The final comparison verifies text and cursor against the baseline.

Single local measurements on a synthetic repaint of 12 rows, one write every 30 ms:

| Measurement | Without DEC 2026 | With DEC 2026 |
| --- | ---: | ---: |
| Snapshots/notifications in the runtime test | 12 | 1 |
| JSON bytes of the snapshot payloads in the runtime test | 842.291 | 126.958 |
| DOM repaints observed in Chrome | 12 | 1 |
| Chrome renderer task time | 46,56 ms | 6,59 ms |
| Chrome renderer script time | 13,46 ms | 1,85 ms |

The bytes are those of the snapshots serialized in the test, not a measurement of the overall WebSocket traffic. The Chrome times are CDP metrics of the single observation window, not percentiles nor a measurement of total server CPU. The two runs have the same final content. The driver excludes the initial `READY` frame, which can be republished by focus without being an intermediate repaint.

Real btop on macOS was also verified: alternate buffer, `CSI ? 2026 h/l` sequences, panels active after resize and return to the shell on exit. The controlled performance comparison remains the synthetic one; no measured percentage reduction is attributed to btop.

To repeat the browser comparison, start isolated Bivium and Chrome as in the acceptance section, then run `node Bivium.Tests/terminal-synchronized-output-acceptance.mjs`. The driver uses port 5186 directly (configurable with `BIVIUM_TEST_PORT`), CDP 9226 and the local Python fixture; it requires neither the proxy nor model CLIs. The test is configured for the local macOS zsh shell and waits for its prompt before sending the command.

## Dynamic palette and underlines — phase 6

Each complete frame exposes the consistent view of `Terminal.Colors.Take()`: 256-color palette, default foreground and background, and cursor color. The renderer uses these values only in the terminal area; title bar, tabs and the rest of Bivium keep the interface theme. History rows keep the indexes and are therefore represented with the current palette of the session, like the terminal buffer.

To avoid repeating the 256 colors in every update, full handoffs and snapshots always include the palette, while an ordinary patch keeps the one already applied by the browser. A revision that contains OSC 4, 10, 11 or 12 includes the new state; the OSC 104 and 110–112 resets follow the same path. The protocol stays consistent with synchronized output: a palette changed inside DEC 2026 becomes visible together with the completed frame.

Cells distinguish single, double, curly, dotted and dashed underline. The color can follow the foreground or be indexed or RGB; in the browser curly uses the CSS `wavy` style. The old underline flag remains in the contract for decorations combined with strikethrough and overline. The reflow checkpoint comparison also includes the new attributes, avoiding treating cells with different underlines as equivalent.

Verified: subsequent changes and palette resets, special colors, the five underline styles and indexed/RGB/default colors. In real Chrome on macOS the following were observed: terminal area `#abcdef`/`#102030`, cursor `#fedcba`, indexed text `#123456` and curly underline `#445566`; after the reset the initial xterm values returned. No changes to the XTerm.NET source.

## OSC 133 prompt navigation — phase 7

Bivium uses the native `PromptStart` OSC 133 markers of XTerm.NET and keeps their column in the row snapshots. The metadata follows the cells during reflow, viewport-to-archive transition and paging; it is not derived from the prompt text. The `ShellIntegrationMarkReceived` events and the native `TryFindPreviousPrompt`/`TryFindNextPrompt` APIs are covered by a dedicated test.

In the normal buffer, `Ctrl+Up arrow` looks for the previous prompt and `Ctrl+Down arrow` for the next one. The server traverses a single timeline made of the external archive and the current screen; the browser brings the found row into the viewport using the existing paging. Successive presses continue from the position reached. Ordinary input, paste, manual scroll and return to the bottom update the navigation anchor.

The shortcut is intercepted only after the session has actually emitted at least one OSC 133 marker and only in the normal buffer. Without shell integration, or inside an application that uses the alternate buffer, `Ctrl+Up/Down arrow` keeps the existing input path to the PTY. The markers stay invisible: no graphical indicators have been added and the text export contains only terminal input/output, without OSC 133 sequences or duplicate rows caused by resize.

The real Chrome run on macOS generated three marked prompts separated by enough output to cross the archive and the current screen. From the bottom, two `Ctrl+Up arrow` presses reached `PROMPT-3` and `PROMPT-2` in order; `Ctrl+Down arrow` returned to `PROMPT-3`. The shell or application must configure and emit OSC 133: Bivium does not automatically modify the zsh, bash or PowerShell profiles.

## Clipboard, notifications, progress and attention — phase 8

The UX and security decisions are applied separately for each capability. The OSC 52 and Kitty OSC 5522 requests can write exclusively `text/plain` UTF-8, up to 1 MiB. Bivium shows a preview and requires the explicit `Copy` gesture; `Cancel` discards the request. Requests are queued with a limit of eight items and the visual preview is limited, while the confirmation copies the full text. Clipboard read stays disabled and binary formats are ignored.

The OSC 9 and Kitty OSC 99 notifications produce text toasts internal to Bivium. At most five toasts remain visible at the same time, they disappear after five seconds and a click opens the terminal and selects the session that generated them. No operating system notifications, sounds, icons or additional browser access are requested.

OSC 9;4 progress belongs to the single session: the indeterminate state shows a spinning icon in its tab, determinate states show the percentage and warning/error use a distinct style. The indicator is removed when the application sends the `None` state or the process terminates. The command bar and the F12 button do not show progress.

An iTerm2 `RequestAttention` request makes the affected tab and `F12 Terminal` blink three times, then keeps the highlight until the session is opened. If the session is already open and active, the request is acknowledged without blinking. Pointer shape stays disabled.

The real Chrome run verified spinner and `42%` in the tab only, toast and session opening, attention on tab/F12 with reset, no blinking on the already active session and clipboard preview. The `Copy` click passed exactly `copy me ✓` to a substitute Clipboard API confined to the test page; the run neither read nor modified the system clipboard.

## Graphics protocols — phase 10

Inline image support is out of scope for Bivium by explicit decision. Sixel, Kitty Graphics and iTerm2 images stay disabled; Bivium does not announce these capabilities, does not decode invisible graphics payloads and does not introduce image transfer, cache or placement in the browser. The phase is closed without application changes.

## Measured optimizations — phase 11

Profiling was performed in Release using the local patterns based on `Stopwatch`, `GC.GetAllocatedBytesForCurrentThread`, real JSON payloads and the benchmark project already present in the fork. The optimizations were kept only when the measurement showed a material benefit.

OSC 8 serialization no longer calls `TryGetLinkAt` for every cell, an operation that in the fork scans all the links of the row linearly. Since `BufferLine.Links` guarantees ranges ordered from left to right, Bivium advances through the list only once while visiting the cells. On 200 snapshots of a 30×119 grid with 20 links per row, the median of three runs went from 92,9 ms to 76,1 ms, about 18% less; allocations and payload are unchanged.

The PTY path now passes the bytes read by `ShellService` directly to `Terminal.Write(ReadOnlySpan<byte>)`. The UTF-8 decoder, the character buffer and the intermediate UTF-16 string were removed from the receive path. In the fork's one-second-per-corpus benchmark, the byte path was 1,22–1,30 times faster on the normal corpora, equivalent on flood and 0,97 times on alternate redraw; it eliminates the ASCII transcoding allocations, while Unicode goes from 5,17 to 4,10 bytes allocated per character. The regression test deliberately splits a UTF-8 sequence in the middle of `👩‍💻` and also verifies OSC 8 in the same stream. A real Chrome run through Blazor and a zsh PTY returned exactly `BYTE-👩‍💻-✓`.

The final measurement distinguishes snapshot construction and JSON serialization:

| Scenario | Snapshot, 200 iterations | Snapshot allocations | JSON, 20 iterations | JSON allocations | Payload |
| --- | ---: | ---: | ---: | ---: | ---: |
| 30×119 without links | 63,6 ms | 67,02 MiB | 33,4 ms | 14,87 MiB | 777.071 bytes |
| 30×119, 20 links per row | 76,0 ms | 58,55 MiB | 29,4 ms | 13,39 MiB | 699.071 bytes |

In the synchronized output test, the same workload produces 12 snapshots, 1.418.939 bytes and 17,961 ms without DEC 2026, against one snapshot, 213.292 bytes and 0,370 ms with DEC 2026. JSON serialization of a full frame remains a visible cost, but synchronized output already eliminates the intermediate frames. A new compact protocol would require a broad change of the frontend/backend contract and is not justified by the current measurements.

Preallocation of the cell array and of the `StringBuilder` capacity was also tried: it reduced allocations by 12–16%, but materially worsened latency, so it was removed. No XTerm.NET source was modified.

## Reflow integrated in the runtime

The gate `XTermPostUpgradeTests.ReflowPreservesCompletedArchiveAndLiveText` now passes. The fix is entirely in Bivium: no changes to the XTerm.NET source, no deletion of the internal scrollback and no raw PTY recording.

`TerminalHistoryReflow` applies to the runtime the projection verified in the 24 tests of `ReflowProvenanceSpikeTests`:

1. Before the resize it freezes the snapshots of the visible rows and the provenance of the cells.
2. For width changes of the normal buffer it builds an equivalent temporary copy. Original row and column are marked in the colors of the copy only.
3. The public `Resize` of the copy transfers the markers along with the cells, without replicating the private algorithm of the library and without searching for equal text.
4. After the real resize it consolidates the fragments that are no longer visible and transfers the provenance to the resulting rows.
5. If clipping removes a suffix, it also consolidates the still-visible prefix: the archive stays ordered and append-only. The export omits the already consolidated cells.

For height-only changes and for the alternate buffer the copy is not needed: row identities are preserved. Both buffers are tracked even while one is inactive.

The screen sent to the renderer keeps the real rows and coordinates of XTerm. Archived cells can therefore become visible again by enlarging the terminal, but they are not saved a second time in the archive or in the export. A recomposed row can contain both archived cells and still-mutable cells.

`LineExitedViewport` consolidates only the fragments not yet archived, both in normal/alternate scroll and in buffer deactivation. The public row cache keeps the provenance bits and is invalidated by writes and recycling. A repaint makes the current state mutable, without causing an append by itself. The existing normal checkpoint comparison remains limited to the same row and the same state; it does not eliminate equal occurrences of output on different rows.

The budgets and the indexes of the segmented archive remain authoritative. The resize also applies the global budget. Cells already discarded by the budget, but still present in the native ring, are not reinserted into the history.

### Hyperlinks and metadata

Serialization reads the native OSC 8 ranges ordered by `BufferLine.Links` with a linear scan per row; the parallel index and the Bivium `HyperlinkChanged` callback have been removed. The tests cover closed and open links, wrapping/reflow, overwrite and erase. For ICH/DCH the current fork erases the links in the moved region: Bivium mirrors this behavior, without attributing obsolete URLs to the cells.

OSC 66 groups are excluded from the reflow of the copy as in the real buffer; cells erased by clipping are not considered survivors. Two cases are covered, including the cutting of a run. This does not add OSC 66 rendering nor enable graphics protocols.

### Verification and cost

The runtime regressions cover:

- the original loss at the archive/viewport boundary;
- genuinely repeated identical rows, wide Unicode, combining, ZWJ and flags;
- archived and mutable fragments in the same row;
- text order and soft-wrap continuations, including cursor clipping;
- TUI repaint, alternate scroll, return to normal and resize of the inactive normal buffer;
- eight height-only grow/shrink cycles without an increase of the final history index;
- recycling of the full ring and keeping the suffix within the budget;
- normal checkpoint unchanged after repaint and resize.

Isolated measurement on this machine, Release, 4.126 initial rows at 120 columns reduced to 80:

| Path | Time | Thread allocations |
| --- | ---: | ---: |
| Full Bivium runtime | 64,8 ms | 55,79 MiB |
| Native `Terminal.Resize` only | 19,4 ms | 20,86 MiB |

These are single measurements on synthetic data, not percentiles or peak memory. Compared with the spike, the per-cell objects and text copies are eliminated; the snapshots are limited to the viewport and height-only resizes avoid the projection. The additional cost of width changes with a full scrollback remains measurable and requires interactive verification; the existing frontend debounce is 50 ms.

The isolated test `NarrowingClipsCursorLineEvenWithoutExternalArchive` keeps documenting the native cursor clipping. Bivium preserves in the archive the fragments that its own resize would make disappear, without changing the geometry produced by the library. The protection introduced concerns `TerminalRuntimeService.ResizeSession`; it does not introduce interception of the internal resizes required by VT sequences.

Still missing: interactive acceptance of the reflow with real applications and cross-platform verification. Codex and Claude are verified for the keyboard; btop is verified for synchronized output. The automated runtime gate is resolved; this does not declare the entire post-upgrade plan complete.

## Remaining work

- Interactive acceptance of the reflow with real applications and latency measurements during continuous resizes.
- Acceptance of Shift+Enter in the SSH/multiplexer combinations in use; local Codex and Claude are verified.
- Further OSC 8 matrix (long URIs, multiple links and alternate); synchronized output, dynamic palette and advanced underlines are implemented.
- Configuration and cross-platform acceptance of the OSC 133 shells; text navigation with `Ctrl+Up/Down arrow` is implemented, while graphical indicators and structured export stay excluded by UX decision.
- Text clipboard write, toast, progress and attention are implemented; clipboard read, system notifications and pointer shape stay disabled by UX decision.
- Sixel, Kitty Graphics and iTerm2 images are excluded by product decision and stay disabled.
- Profiling and measured optimizations are complete; a compact snapshot protocol stays out of scope until a real workload demonstrates the need.
- Recovery with hidden page and production reverse proxy, diagnosis of the original disconnection and cross-platform tests; local 1001 recovery and expired circuit are verified.

## Local verification

```sh
dotnet test Bivium.Tests/Bivium.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
node --test Bivium.Tests/connection.test.mjs Bivium.Tests/terminal-virtualization.test.mjs Bivium.Tests/upload-selection.test.mjs
dotnet test Bivium.Tests/Bivium.Tests.csproj --no-restore --filter FullyQualifiedName~ReflowProvenanceSpikeTests -m:1 -p:UseSharedCompilation=false
```

Final result after phase 11: 124 .NET tests passed and 3 JavaScript scripts passed. Phase 5 adds 13 .NET cases and a dedicated browser run; phase 6 adds seven .NET cases, JavaScript renderer checks and a real Chrome acceptance; phase 7 adds four .NET cases, JavaScript coverage of the shortcut and a real Chrome run through archive and screen; phase 8 adds three .NET cases and a Chrome acceptance of the UX surfaces; phase 10 is closed without code; phase 11 adds the permanent snapshot/JSON measurement, the UTF-8/OSC byte regression and a real PTY run. The 24 spike tests are included in the .NET suite; the 27 `ByteWriteParityTests` cases of the fork also pass. Build succeeded without warnings or errors in the Bivium project; the isolated Release measurement of the reflow and those of phase 11 are reported above. Also verified the real browser runs for keyboard, 1001 recovery, expired circuit, synchronized output, colors, OSC 133 navigation, terminal protocol UX and PTY byte path. No Git/GH command was executed and no fork source was modified.

### Reproducible browser acceptance

The two acceptance scripts must be run explicitly, on an isolated Bivium instance. They do not start Codex or Claude and do not send requests to model services.

1. Start Bivium with temporary home and data directories, using the Kestrel endpoint `http://127.0.0.1:5186`.
2. Start Chrome with a temporary profile and remote debugging on `127.0.0.1:9226`.
3. Start `node Bivium.Tests/terminal-reconnect-proxy.mjs`, which listens exclusively on loopback at port 5187.
4. Run `node Bivium.Tests/terminal-browser-acceptance.mjs` for keyboard and 1001 recovery.
5. Run `node Bivium.Tests/terminal-browser-acceptance.mjs --expired-circuit` for 205 seconds of unavailability and fallback after circuit expiry.
6. Terminate the test processes and, if desired, remove the temporary directories.

The driver requires Node with global `fetch` and `WebSocket` (verified with Node 26). The ports are configurable with `BIVIUM_TEST_PORT`, `BIVIUM_TEST_PROXY_PORT` and `BIVIUM_TEST_CDP_PORT`. The proxy and its control endpoints are test fixtures only and are not part of the application.
