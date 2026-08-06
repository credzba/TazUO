# Auto-Reconnect Stuck at VerifyingAccount After Server Reset

## Symptoms

When auto-reconnect is enabled and the server resets (e.g., daily 1 AM restart), the client
enters an infinite reconnect loop. The console repeatedly shows:

```
[LoginHandshake.HandleReconnect] Reconnecting...
[LoginHandshake.Connect] Start login to: shard.uoeventine.com,2593
[LoginHandshake.OnNetClientConnected] Connected
[LoginHandshake.SetLoginStep] Set login step to VerifyingAccount.
...
[PacketParser.ParsePackets] Need more data ID: AE | off: 3 | len: 43008 | stream.pos: 2071
...
[LoginHandshake.CheckHandshakeTimeout] Handshake timed out at step VerifyingAccount, aborting connection attempt.
[LoginHandshake.Disconnect] Disconnecting...
```

The cycle repeats every ~5 minutes with exponentially increasing backoff. The key observations:

- The `stream.pos` (bytes accumulated in the parse buffer) grows by ~46 bytes each cycle:
  `2071 → 2117 → 2163 → 2209 → ...`
- The parser is stuck on packet `0xAE` (Unicode chat) requesting 43,008 bytes
- The step remains `VerifyingAccount` when the watchdog fires
- A separate client process can connect without issue during this time

## Root Cause

**The `PacketParser` singleton's internal `CircularBuffer` (`_buffer`) accumulates stale packet
data across reconnection attempts and is never cleared.**

### Detailed flow:

1. **Game session active** — the client is connected to the game server, receiving normal
   game packets. `PacketParser._buffer` processes these packets, consuming them as they
   arrive.

2. **Server disconnects (reset)** — the TCP connection drops. Any partially-processed or
   buffered game data remains in `PacketParser._buffer`. For example, a `0xAE` (Unicode
   chat) packet that was mid-reception.

3. **Reconnect triggers** — `LoginHandshake.HandleReconnect()` calls `Connect()` which
   creates a new `AsyncNetClient`. The new `AsyncNetClient` has a fresh internal
   `_incomingMessages` queue. **However, `PacketParser._buffer` is NOT cleared.**

4. **New connection established** — TCP connects, `OnNetClientConnected` fires, seed and
   first-login packets are sent. The server responds with `0xA8` (server list).

5. **Data appended to stale buffer** — `GameController.ProcessNetworkPackets()` dequeues
   the new login response data and feeds it to `PacketParser.ParsePackets()`, which
   **appends it to the existing `_buffer`**. The buffer now contains:
   ```
   [old game data] + [new login data]
   ```

6. **Parser gets stuck** — `ParsePackets` iterates from position 0. The first byte is
   stale game data (e.g., `0xAE`). For variable-length packets like `0xAE`, the length
   is read from bytes 1-2 as a big-endian uint16. The stale byte at position 1-2 reads
   as `0xA800` (43,008 bytes). The parser checks if the buffer has 43,008 bytes — it
   doesn't (only ~2,000). It breaks, waiting for more data.

7. **New server list response blocked** — The `0xA8` server list packet that arrived
   with the new connection sits behind the stuck `0xAE` in the buffer and is **never
   processed**. The login step stays at `VerifyingAccount`.

8. **Watchdog fires** — After 8 seconds, `CheckHandshakeTimeout` fires because the step
   is still `VerifyingAccount`. It disconnects and calls `HandleConnectionFailure`,
   which sets `Reconnect = true` and step to `PopUpMessage`.

9. **Cycle repeats** — `HandleReconnect` triggers again, calling `Connect()` → goto step 3.
   Each cycle appends ~46 more bytes to the cumulative buffer without clearing.

### Why local network disconnect works but server reset doesn't

- **Local network disconnect**: The client's TCP stack detects the connection is lost
  quickly. The internal socket disconnected event fires. By the time reconnect happens,
  the buffer may be empty or contain well-formed data that the parser can handle.
  
- **Server reset**: The server may send some final packets (system messages, chat) before
  the connection drops, populating the buffer with game data. Some packets (like `0xAE`)
  are variable-length and the parser interprets their length from stale bytes, creating
  an impossible length requirement.

### Secondary issue: Missing `Connecting` step on reconnect

`LoginHandshake.Connect()` skipped calling `SetLoginStep(LoginSteps.Connecting)` when
`Reconnect` was true. This meant:

- The handshake watchdog (`CheckHandshakeTimeout`) was **not armed** during the TCP
  connect phase, only after `OnNetClientConnected` fired.
- The re-entry guard `if (CurrentLoginStep == LoginSteps.Connecting) return;` was
  ineffective during reconnect, as the step lingered at `PopUpMessage`.

## Fix

Three changes in two files:

### 1. `src/ClassicUO.Client/Network/PacketHandlers/PacketParser.cs`

Added `ClearBuffers()` method to flush the internal parse buffers:

```csharp
public void ClearBuffers()
{
    lock (_buffer) { _buffer.Clear(); }
    lock (_pluginsBuffer) { _pluginsBuffer.Clear(); }
}
```

### 2. `src/ClassicUO.Client/Network/LoginHandshake.cs`

**a) Clear stale buffer on every new connection** (`Connect()`):

```csharp
PacketParser.Instance.ClearBuffers();  // before any connection state change
```

**b) Clear buffer on game server relay** (`AfterRelayConnect()`):

```csharp
PacketParser.Instance.ClearBuffers();  // after creating new AsyncNetClient
```

**c) Always set `LoginSteps.Connecting` regardless of `Reconnect` status**:

Previously:
```csharp
if (!Reconnect)
{
    SetLoginStep(LoginSteps.Connecting);
    _reconnectTryCounter = 1;
}
```

Now:
```csharp
if (!Reconnect)
{
    _reconnectTryCounter = 1;
}

SetLoginStep(LoginSteps.Connecting);  // always, to arm watchdog + guard re-entry
```

## Verification

- Build compiles with 0 errors
- The `ClearBuffers()` call in `Connect()` ensures every new connection attempt starts
  with a fresh parse buffer, eliminating the stale-data poisoning described above
- The `ClearBuffers()` call in `AfterRelayConnect()` handles the game server relay
  transition, where a new server connection is established mid-handshake
- The unconditional `SetLoginStep(LoginSteps.Connecting)` ensures the watchdog is
  armed during TCP connect and prevents re-entry via the guard at the top of `Connect()`
