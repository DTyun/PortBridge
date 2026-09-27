# v1.1 verification

## v1.4.1 single-port save fix

The settings parser, the real WinForms Save button, XML persistence and re-import were exercised with `7890`, with `7890,` left after removing a second port, and with switching from two ports to one. The trailing comma is now normalized on save. Empty middle entries and repeated ports remain invalid. The complete suite passed 74 checks.

## v1.3 update

64 checks passed. Added multi-port TCP/UDP forwarding, aggregated counters, group stop/restart, failure on a later port with complete rollback, comma and Chinese comma parsing, invalid/duplicate/empty input rejection, multi-port XML roundtrip and legacy single-port compatibility. HTTP CONNECT and SOCKS5 diagnostics verify both listening ports; a missing second port correctly prevents an overall pass. Existing relay, config, native window and diagnostic checks remain passing.

## v1.4 update

Redesigned the main WinForms screen for beginner use: explicit numbered route steps, plain-language helper text, grouped protocol/startup options, one primary start action, secondary configuration actions, and a dedicated log card. Rendered the updated 820×860 window at 100% scaling and corrected the action-row height so all controls remain fully visible. Existing 64 runtime checks remain the regression suite.

## v1.2 update

`test.ps1` passed all 37 checks. Added configuration roundtrip, overwrite, invalid export preservation, invalid import rejection, XML external entity rejection and bundled example parsing. Inspected updated main form render with two configuration buttons. v1.2 package includes the executable, README and PortBridge.config.xml. Existing user settings take priority over the bundled example.

- `test.ps1`: 31 checks passed: existing TCP/UDP forwarding and window lifecycle, HTTP CONNECT success, SOCKS5 fallback success, HTTP 503 failure, temporary relay cleanup, preservation of running relays, invalid URL, TCP disabled, cancellation and test dialog failure rendering.
- Inspected main form and diagnostic dialog renders. All four main action buttons fit at the default window size.
- Live diagnostic executed in the normal Windows user environment against the existing running relay `127.0.0.1:7890 -> 127.0.0.1:25378`. HTTP CONNECT succeeded; HTTPS handshake and certificate validation succeeded; `https://www.gstatic.com/generate_204` returned HTTP 204. Total about 1.2 seconds.
- Restricted execution initially failed at Windows TLS credential access; normal-user verification succeeded without any certificate-validation bypass.
- Existing running v1.0 was not stopped or replaced. Updated executable is `dist/PortBridge-v1.1.exe`. The user must exit v1.0 from its tray menu before launching v1.1 because the app is single-instance.
- Diagnostic scope: TCP HTTP/HTTPS through HTTP CONNECT or unauthenticated SOCKS5. It does not verify UDP, authenticated proxy setups, or reachability of every external website.
