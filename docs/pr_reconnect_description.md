## Description
Auto-reconnect enters an infinite loop after server resets. The `PacketParser` singleton's internal `CircularBuffer` accumulates stale packet data across reconnection attempts. When the old connection drops, leftover game data (e.g. a `0xAE` Unicode chat packet) remains in the buffer. On reconnect, new login-handshake data is appended to this stale buffer, causing the parser to interpret old garbage bytes as a packet length of 43,008 and block forever — preventing the new `0xA8` server list response from being processed. The handshake watchdog fires at `VerifyingAccount` and the cycle repeats indefinitely.

Additionally, `LoginHandshake.Connect()` skipped setting `LoginSteps.Connecting` when `Reconnect` was true, leaving the watchdog disarmed during TCP connect and the re-entry guard ineffective.

## Type of Change
- [x] Bug fix
- [ ] New feature
- [ ] Performance improvement
- [ ] Code refactoring
- [ ] Documentation update

## Testing
- Reproduced by disconnecting from a live server (server reset / daily restart scenario) — confirmed the client enters the infinite `VerifyingAccount` → timeout → reconnect loop
- Applied fix, repeated the test — client now reconnects successfully on first attempt after server comes back up
- Tested normal login flow (non-reconnect) — no regression
- Tested game-server relay connection during login — no regression
- Build compiles with 0 errors

## Screenshots (if applicable)
N/A

## Additional Notes
**Root cause**: `PacketParser._buffer` (singleton `CircularBuffer`) was never cleared between reconnection attempts. `AsyncNetClient` is recreated with a fresh internal queue, but any data already drained into `PacketParser._buffer` from the previous connection persists and poisons subsequent parsing.

**Fix summary**:
- Added `PacketParser.ClearBuffers()` to flush `_buffer` and `_pluginsBuffer`
- Call it in `LoginHandshake.Connect()` before each new connection
- Call it in `LoginHandshake.AfterRelayConnect()` before game server relay
- Always set `LoginSteps.Connecting` in `Connect()` to arm watchdog and guard re-entry

See `reconnect_error.md` for the full root cause analysis with log excerpts.
