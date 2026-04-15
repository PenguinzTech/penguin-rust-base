# WAF Detectors Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 7 new network-layer detection modules to the Go WAF sidecar — all operable in pure vanilla mode without Oxide.

**Architecture:** Each detector is an independent struct in `waf/internal/detect/`, unit-tested in isolation. All detectors are wired into `proxy.Pipeline` in a single integration task. The UDP proxy's response-forwarding path is fixed as part of the amplification guard task (currently responses are discarded). New env vars are added to `cmd/waf/main.go`. Docs updated last.

**Tech Stack:** Go 1.24.2, `github.com/oschwald/geoip2-golang` (Task 7 only), existing Prometheus metrics pattern.

---

## File Map

| Action | Path | Responsibility |
|---|---|---|
| Create | `waf/internal/detect/reconnect.go` | Reconnect storm per SteamID |
| Create | `waf/internal/detect/reconnect_test.go` | Unit tests |
| Create | `waf/internal/detect/handshake.go` | Incomplete handshake flood |
| Create | `waf/internal/detect/handshake_test.go` | Unit tests |
| Create | `waf/internal/detect/entropy.go` | High-entropy payload anomaly |
| Create | `waf/internal/detect/entropy_test.go` | Unit tests |
| Create | `waf/internal/detect/ipchurn.go` | Per-SteamID IP velocity |
| Create | `waf/internal/detect/ipchurn_test.go` | Unit tests |
| Create | `waf/internal/detect/burst.go` | Sliding-window burst detection |
| Create | `waf/internal/detect/burst_test.go` | Unit tests |
| Create | `waf/internal/detect/amplification.go` | Query-port amplification ratio |
| Create | `waf/internal/detect/amplification_test.go` | Unit tests |
| Create | `waf/internal/detect/geovelocity.go` | Geo-impossible travel detection |
| Create | `waf/internal/detect/geovelocity_test.go` | Unit tests |
| Modify | `waf/internal/proxy/pipeline.go` | Add 7 new detector fields |
| Modify | `waf/internal/proxy/udp.go` | Wire detectors + fix response forwarding |
| Modify | `waf/internal/metrics/metrics.go` | Add 7 new metrics |
| Modify | `waf/cmd/waf/main.go` | Parse new env vars, construct detectors |
| Modify | `waf/go.mod` + `waf/go.sum` | Add geoip2-golang dep (Task 7) |
| Modify | `waf/README.md` | Document new protections |
| Modify | `docs/waf.md` | Document new protections + env vars |

---

## Task 1: ReconnectDetector

Tracks how many times a SteamID authenticates within a rolling window. Repeated reconnects crash-exploit the server's connection slot table.

**Files:**
- Create: `waf/internal/detect/reconnect.go`
- Create: `waf/internal/detect/reconnect_test.go`

- [ ] **Step 1: Write failing test**

```go
// waf/internal/detect/reconnect_test.go
package detect

import (
	"net"
	"testing"
	"time"
)

func TestReconnectDetector_BelowThreshold(t *testing.T) {
	d := NewReconnectDetector(5, time.Minute)
	ip := net.ParseIP("1.2.3.4")
	const steamID = uint64(76561198000000001)
	for i := 0; i < 5; i++ {
		if d.RecordAuth(ip, steamID) {
			t.Fatalf("should not trigger on attempt %d", i+1)
		}
	}
}

func TestReconnectDetector_ExceedsThreshold(t *testing.T) {
	d := NewReconnectDetector(3, time.Minute)
	ip := net.ParseIP("1.2.3.4")
	const steamID = uint64(76561198000000002)
	triggered := false
	for i := 0; i < 6; i++ {
		if d.RecordAuth(ip, steamID) {
			triggered = true
			break
		}
	}
	if !triggered {
		t.Fatal("expected storm detection after exceeding threshold")
	}
}

func TestReconnectDetector_WindowExpiry(t *testing.T) {
	d := NewReconnectDetector(2, 100*time.Millisecond)
	ip := net.ParseIP("1.2.3.4")
	const steamID = uint64(76561198000000003)
	d.RecordAuth(ip, steamID)
	d.RecordAuth(ip, steamID)
	time.Sleep(150 * time.Millisecond)
	// Window expired; count resets
	if d.RecordAuth(ip, steamID) {
		t.Fatal("should not trigger after window expiry")
	}
}

func TestReconnectDetector_ZeroSteamID(t *testing.T) {
	d := NewReconnectDetector(1, time.Minute)
	ip := net.ParseIP("1.2.3.4")
	// SteamID=0 means pre-auth packet; should never trigger
	for i := 0; i < 100; i++ {
		if d.RecordAuth(ip, 0) {
			t.Fatal("should not trigger for zero SteamID")
		}
	}
}

func TestReconnectDetector_Disabled(t *testing.T) {
	d := NewReconnectDetector(0, time.Minute)
	ip := net.ParseIP("1.2.3.4")
	const steamID = uint64(76561198000000004)
	for i := 0; i < 100; i++ {
		if d.RecordAuth(ip, steamID) {
			t.Fatal("should not trigger when maxPerWindow=0 (disabled)")
		}
	}
}

func TestReconnectDetector_IndependentSteamIDs(t *testing.T) {
	d := NewReconnectDetector(3, time.Minute)
	ip := net.ParseIP("1.2.3.4")
	const steamA = uint64(76561198000000005)
	const steamB = uint64(76561198000000006)
	// Fill steamA to threshold
	d.RecordAuth(ip, steamA)
	d.RecordAuth(ip, steamA)
	d.RecordAuth(ip, steamA)
	// steamB should be unaffected
	if d.RecordAuth(ip, steamB) {
		t.Fatal("steamB should not be affected by steamA's count")
	}
}
```

- [ ] **Step 2: Run test to confirm it fails**

```bash
cd waf && go test ./internal/detect/ -run TestReconnectDetector -v
```
Expected: `cannot find package` or `undefined: NewReconnectDetector`

- [ ] **Step 3: Write implementation**

```go
// waf/internal/detect/reconnect.go
package detect

import (
	"net"
	"sync"
	"time"
)

// ReconnectDetector tracks authentication handshake frequency per SteamID.
// A SteamID authenticating more than maxPerWindow times within window is a
// reconnect storm — typically a crash-exploit reconnect loop.
type ReconnectDetector struct {
	maxPerWindow int
	window       time.Duration
	sessions     sync.Map // uint64 → *reconnectRecord
}

type reconnectRecord struct {
	timestamps []time.Time
	mu         sync.Mutex
}

// NewReconnectDetector creates a ReconnectDetector. Set maxPerWindow=0 to disable.
func NewReconnectDetector(maxPerWindow int, window time.Duration) *ReconnectDetector {
	return &ReconnectDetector{
		maxPerWindow: maxPerWindow,
		window:       window,
	}
}

// RecordAuth records an authentication handshake for steamID.
// Returns true if a reconnect storm is detected (count > maxPerWindow in window).
// steamID=0 is always ignored (pre-auth packets have no SteamID yet).
func (r *ReconnectDetector) RecordAuth(ip net.IP, steamID uint64) bool {
	if r.maxPerWindow <= 0 || steamID == 0 {
		return false
	}

	val, _ := r.sessions.LoadOrStore(steamID, &reconnectRecord{})
	record := val.(*reconnectRecord)

	record.mu.Lock()
	defer record.mu.Unlock()

	now := time.Now()
	cutoff := now.Add(-r.window)

	// Prune timestamps outside the window
	kept := record.timestamps[:0]
	for _, ts := range record.timestamps {
		if ts.After(cutoff) {
			kept = append(kept, ts)
		}
	}
	record.timestamps = append(kept, now)

	return len(record.timestamps) > r.maxPerWindow
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
cd waf && go test ./internal/detect/ -run TestReconnectDetector -v
```
Expected: all 6 tests PASS

- [ ] **Step 5: Commit**

```bash
cd waf && git add internal/detect/reconnect.go internal/detect/reconnect_test.go
git commit -m "feat(waf): reconnect storm detector"
```

---

## Task 2: HandshakeTracker

IPs that send initial packets but never complete Steam authentication are connection-exhaustion probes. Track incomplete vs. completed handshakes per IP.

**Files:**
- Create: `waf/internal/detect/handshake.go`
- Create: `waf/internal/detect/handshake_test.go`

- [ ] **Step 1: Write failing test**

```go
// waf/internal/detect/handshake_test.go
package detect

import (
	"net"
	"testing"
	"time"
)

func TestHandshakeTracker_NormalFlow(t *testing.T) {
	h := NewHandshakeTracker(5, 10*time.Second)
	ip := net.ParseIP("2.2.2.2")
	// Packet arrives, then handshake completes — should never trigger
	h.RecordPacket(ip)
	h.RecordCompletion(ip)
	if h.RecordPacket(ip) {
		t.Fatal("completed handshake should not trigger")
	}
}

func TestHandshakeTracker_IncompleteFlood(t *testing.T) {
	h := NewHandshakeTracker(3, 10*time.Second)
	ip := net.ParseIP("3.3.3.3")
	triggered := false
	for i := 0; i < 10; i++ {
		if h.RecordPacket(ip) {
			triggered = true
			break
		}
	}
	if !triggered {
		t.Fatal("expected flood detection for never-completing handshake")
	}
}

func TestHandshakeTracker_DifferentIPs(t *testing.T) {
	h := NewHandshakeTracker(3, 10*time.Second)
	ip1 := net.ParseIP("4.4.4.4")
	ip2 := net.ParseIP("5.5.5.5")
	// Fill ip1
	for i := 0; i < 10; i++ {
		h.RecordPacket(ip1)
	}
	// ip2 should be unaffected
	if h.RecordPacket(ip2) {
		t.Fatal("ip2 should not be affected by ip1's count")
	}
}

func TestHandshakeTracker_Disabled(t *testing.T) {
	h := NewHandshakeTracker(0, 10*time.Second)
	ip := net.ParseIP("6.6.6.6")
	for i := 0; i < 100; i++ {
		if h.RecordPacket(ip) {
			t.Fatal("should not trigger when maxPending=0 (disabled)")
		}
	}
}

func TestHandshakeTracker_NilIP(t *testing.T) {
	h := NewHandshakeTracker(3, 10*time.Second)
	if h.RecordPacket(nil) {
		t.Fatal("nil IP should never trigger")
	}
}

func TestHandshakeTracker_CompletionResetsCount(t *testing.T) {
	h := NewHandshakeTracker(3, 10*time.Second)
	ip := net.ParseIP("7.7.7.7")
	h.RecordPacket(ip)
	h.RecordPacket(ip)
	h.RecordCompletion(ip) // reset
	// After reset, should be able to send 3 more without triggering
	for i := 0; i < 3; i++ {
		if h.RecordPacket(ip) {
			t.Fatalf("should not trigger after completion reset (attempt %d)", i+1)
		}
	}
}
```

- [ ] **Step 2: Run test to confirm it fails**

```bash
cd waf && go test ./internal/detect/ -run TestHandshakeTracker -v
```

- [ ] **Step 3: Write implementation**

```go
// waf/internal/detect/handshake.go
package detect

import (
	"net"
	"sync"
	"time"
)

// HandshakeTracker detects IPs that repeatedly initiate connections but never
// complete Steam authentication. These are connection-exhaustion flood tools.
type HandshakeTracker struct {
	maxPending int
	timeout    time.Duration
	pending    sync.Map // string(ip) → *handshakeRecord
}

type handshakeRecord struct {
	attempts  int
	completed bool
	firstSeen time.Time
	mu        sync.Mutex
}

// NewHandshakeTracker creates a HandshakeTracker. Set maxPending=0 to disable.
func NewHandshakeTracker(maxPending int, timeout time.Duration) *HandshakeTracker {
	return &HandshakeTracker{
		maxPending: maxPending,
		timeout:    timeout,
	}
}

// RecordPacket records a packet from ip. Returns true if the IP has too many
// incomplete handshakes (flood detected). Thread-safe.
func (h *HandshakeTracker) RecordPacket(ip net.IP) bool {
	if h.maxPending <= 0 || ip == nil {
		return false
	}

	ipStr := ip.String()
	now := time.Now()

	val, loaded := h.pending.LoadOrStore(ipStr, &handshakeRecord{
		attempts:  1,
		firstSeen: now,
	})
	if !loaded {
		return false // first packet, count=1, can't trigger yet
	}

	record := val.(*handshakeRecord)
	record.mu.Lock()
	defer record.mu.Unlock()

	if record.completed {
		// Previous handshake completed — reset for new session
		record.attempts = 1
		record.completed = false
		record.firstSeen = now
		return false
	}

	// If window expired without completion, treat as a new probe window
	if time.Since(record.firstSeen) > h.timeout {
		record.firstSeen = now
		// Carry count forward so persistent probers accumulate
	}

	record.attempts++
	return record.attempts > h.maxPending
}

// RecordCompletion marks the handshake for ip as complete (SteamID extracted).
// Resets the incomplete counter for this IP.
func (h *HandshakeTracker) RecordCompletion(ip net.IP) {
	if ip == nil {
		return
	}
	val, ok := h.pending.Load(ip.String())
	if !ok {
		return
	}
	record := val.(*handshakeRecord)
	record.mu.Lock()
	record.completed = true
	record.attempts = 0
	record.mu.Unlock()
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
cd waf && go test ./internal/detect/ -run TestHandshakeTracker -v
```
Expected: all 6 tests PASS

- [ ] **Step 5: Commit**

```bash
cd waf && git add internal/detect/handshake.go internal/detect/handshake_test.go
git commit -m "feat(waf): incomplete handshake flood detector"
```

---

## Task 3: EntropyDetector

Shannon entropy of payload bytes (max 8 bits). Legitimate Rust packets have structured, lower-entropy content. Exploit tools and garbage-flood scripts produce near-random payloads.

**Files:**
- Create: `waf/internal/detect/entropy.go`
- Create: `waf/internal/detect/entropy_test.go`

- [ ] **Step 1: Write failing test**

```go
// waf/internal/detect/entropy_test.go
package detect

import (
	"math/rand"
	"net"
	"testing"
)

func TestEntropyDetector_LowEntropyPayload(t *testing.T) {
	d := NewEntropyDetector(7.5, 16)
	ip := net.ParseIP("1.1.1.1")
	// Repeated byte = entropy ~0
	payload := make([]byte, 64)
	for i := range payload {
		payload[i] = 0xAA
	}
	if d.Check(ip, 0, payload) != nil {
		t.Fatal("low-entropy payload should not trigger")
	}
}

func TestEntropyDetector_HighEntropyPayload(t *testing.T) {
	d := NewEntropyDetector(7.5, 16)
	ip := net.ParseIP("1.1.1.2")
	// Near-random payload — all 256 byte values distributed evenly
	payload := make([]byte, 512)
	for i := range payload {
		payload[i] = byte(i % 256)
	}
	det := d.Check(ip, 0, payload)
	if det == nil {
		t.Fatal("near-uniform payload should trigger high entropy detection")
	}
	if det.Heuristic != "high_entropy" {
		t.Fatalf("expected heuristic 'high_entropy', got %q", det.Heuristic)
	}
}

func TestEntropyDetector_PayloadTooSmall(t *testing.T) {
	d := NewEntropyDetector(7.5, 64)
	ip := net.ParseIP("1.1.1.3")
	// Random but too small to analyze
	payload := make([]byte, 32)
	rand.Read(payload)
	if d.Check(ip, 0, payload) != nil {
		t.Fatal("payload below minPayloadSize should not trigger")
	}
}

func TestEntropyDetector_Disabled(t *testing.T) {
	d := NewEntropyDetector(0, 64) // threshold=0 disables
	ip := net.ParseIP("1.1.1.4")
	payload := make([]byte, 256)
	rand.Read(payload)
	if d.Check(ip, 0, payload) != nil {
		t.Fatal("disabled detector should never trigger")
	}
}

func TestEntropyDetector_DetectionCarriesSteamID(t *testing.T) {
	d := NewEntropyDetector(7.0, 16)
	ip := net.ParseIP("1.1.1.5")
	const steamID = uint64(76561198000000007)
	payload := make([]byte, 512)
	for i := range payload {
		payload[i] = byte(i % 256)
	}
	det := d.Check(ip, steamID, payload)
	if det == nil {
		t.Fatal("expected detection")
	}
	if det.SteamID != steamID {
		t.Fatalf("expected SteamID %d in detection, got %d", steamID, det.SteamID)
	}
}

func TestShannonEntropy_AllSame(t *testing.T) {
	data := make([]byte, 100)
	// All zeros → entropy = 0
	e := shannonEntropy(data)
	if e != 0.0 {
		t.Fatalf("expected entropy 0 for uniform data, got %f", e)
	}
}

func TestShannonEntropy_MaxEntropy(t *testing.T) {
	// All 256 values equally distributed → entropy ≈ 8.0
	data := make([]byte, 256)
	for i := range data {
		data[i] = byte(i)
	}
	e := shannonEntropy(data)
	if e < 7.99 || e > 8.01 {
		t.Fatalf("expected entropy ≈ 8.0, got %f", e)
	}
}
```

- [ ] **Step 2: Run test to confirm it fails**

```bash
cd waf && go test ./internal/detect/ -run "TestEntropyDetector|TestShannonEntropy" -v
```

- [ ] **Step 3: Write implementation**

```go
// waf/internal/detect/entropy.go
package detect

import (
	"fmt"
	"math"
	"net"
)

// EntropyDetector flags packets whose byte entropy exceeds a threshold.
// Legitimate Rust game packets have structured content (protocol headers,
// varint-encoded fields) with entropy well below the maximum of 8 bits.
// Exploit tools and garbage-flood scripts produce near-random payloads.
type EntropyDetector struct {
	threshold      float64 // bits; max theoretical = 8.0; default 7.5
	minPayloadSize int     // skip payloads smaller than this; default 64
}

// NewEntropyDetector creates an EntropyDetector. Set threshold=0 to disable.
func NewEntropyDetector(threshold float64, minPayloadSize int) *EntropyDetector {
	return &EntropyDetector{
		threshold:      threshold,
		minPayloadSize: minPayloadSize,
	}
}

// Check analyzes payload entropy. Returns a Detection if entropy >= threshold.
// Returns nil if disabled, payload too small, or entropy is within normal range.
func (e *EntropyDetector) Check(ip net.IP, steamID uint64, payload []byte) *Detection {
	if e.threshold <= 0 || len(payload) < e.minPayloadSize {
		return nil
	}

	entropy := shannonEntropy(payload)
	if entropy < e.threshold {
		return nil
	}

	return &Detection{
		Heuristic: "high_entropy",
		IP:        ip,
		SteamID:   steamID,
		Detail:    fmt.Sprintf("entropy %.4f bits (threshold %.4f, payload %d bytes)", entropy, e.threshold, len(payload)),
	}
}

// shannonEntropy computes Shannon entropy of data in bits per byte (range 0–8).
func shannonEntropy(data []byte) float64 {
	if len(data) == 0 {
		return 0
	}
	var counts [256]int
	for _, b := range data {
		counts[b]++
	}
	n := float64(len(data))
	entropy := 0.0
	for _, c := range counts {
		if c == 0 {
			continue
		}
		p := float64(c) / n
		entropy -= p * math.Log2(p)
	}
	return entropy
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
cd waf && go test ./internal/detect/ -run "TestEntropyDetector|TestShannonEntropy" -v
```
Expected: all 7 tests PASS

- [ ] **Step 5: Commit**

```bash
cd waf && git add internal/detect/entropy.go internal/detect/entropy_test.go
git commit -m "feat(waf): payload entropy anomaly detector"
```

---

## Task 4: IPChurnDetector

A single SteamID appearing from many distinct IPs within a time window indicates VPN-hopping ban evasion or a credential-shared botnet account.

**Files:**
- Create: `waf/internal/detect/ipchurn.go`
- Create: `waf/internal/detect/ipchurn_test.go`

- [ ] **Step 1: Write failing test**

```go
// waf/internal/detect/ipchurn_test.go
package detect

import (
	"fmt"
	"net"
	"testing"
	"time"
)

func TestIPChurnDetector_BelowThreshold(t *testing.T) {
	d := NewIPChurnDetector(5, time.Minute)
	const steamID = uint64(76561198000000010)
	for i := 0; i < 4; i++ {
		ip := net.ParseIP(fmt.Sprintf("10.0.0.%d", i+1))
		if d.Check(steamID, ip) != nil {
			t.Fatalf("should not trigger at IP %d (below threshold of 5)", i+1)
		}
	}
}

func TestIPChurnDetector_ExceedsThreshold(t *testing.T) {
	d := NewIPChurnDetector(3, time.Minute)
	const steamID = uint64(76561198000000011)
	var det *Detection
	for i := 0; i < 5; i++ {
		ip := net.ParseIP(fmt.Sprintf("10.1.0.%d", i+1))
		det = d.Check(steamID, ip)
		if det != nil {
			break
		}
	}
	if det == nil {
		t.Fatal("expected detection after exceeding IP threshold")
	}
	if det.Heuristic != "ip_churn" {
		t.Fatalf("expected heuristic 'ip_churn', got %q", det.Heuristic)
	}
}

func TestIPChurnDetector_WindowExpiry(t *testing.T) {
	d := NewIPChurnDetector(2, 100*time.Millisecond)
	const steamID = uint64(76561198000000012)
	d.Check(steamID, net.ParseIP("10.2.0.1"))
	d.Check(steamID, net.ParseIP("10.2.0.2"))
	time.Sleep(150 * time.Millisecond) // window expires
	// Both old IPs pruned; this should not trigger
	if d.Check(steamID, net.ParseIP("10.2.0.3")) != nil {
		t.Fatal("should not trigger after window expiry prunes old IPs")
	}
}

func TestIPChurnDetector_SameIPRepeated(t *testing.T) {
	d := NewIPChurnDetector(3, time.Minute)
	const steamID = uint64(76561198000000013)
	ip := net.ParseIP("10.3.0.1")
	for i := 0; i < 20; i++ {
		if d.Check(steamID, ip) != nil {
			t.Fatal("same IP repeated should not increment distinct-IP count")
		}
	}
}

func TestIPChurnDetector_ZeroSteamID(t *testing.T) {
	d := NewIPChurnDetector(1, time.Minute)
	for i := 0; i < 10; i++ {
		ip := net.ParseIP(fmt.Sprintf("10.4.0.%d", i+1))
		if d.Check(0, ip) != nil {
			t.Fatal("zero SteamID should never trigger")
		}
	}
}

func TestIPChurnDetector_IndependentSteamIDs(t *testing.T) {
	d := NewIPChurnDetector(2, time.Minute)
	const steamA = uint64(76561198000000014)
	const steamB = uint64(76561198000000015)
	d.Check(steamA, net.ParseIP("10.5.0.1"))
	d.Check(steamA, net.ParseIP("10.5.0.2"))
	d.Check(steamA, net.ParseIP("10.5.0.3")) // triggers steamA
	// steamB should be clean
	if d.Check(steamB, net.ParseIP("10.5.0.4")) != nil {
		t.Fatal("steamB should not be affected by steamA's churn")
	}
}
```

- [ ] **Step 2: Run test to confirm it fails**

```bash
cd waf && go test ./internal/detect/ -run TestIPChurnDetector -v
```

- [ ] **Step 3: Write implementation**

```go
// waf/internal/detect/ipchurn.go
package detect

import (
	"fmt"
	"net"
	"sync"
	"time"
)

// IPChurnDetector detects a single SteamID appearing from too many distinct
// IPs within a time window — indicating VPN-hopping or a credential-shared
// botnet account attempting ban evasion.
type IPChurnDetector struct {
	maxIPs int
	window time.Duration
	state  sync.Map // uint64 → *churnRecord
}

type churnRecord struct {
	ips map[string]time.Time // ip string → last seen timestamp
	mu  sync.Mutex
}

// NewIPChurnDetector creates an IPChurnDetector. Set maxIPs=0 to disable.
func NewIPChurnDetector(maxIPs int, window time.Duration) *IPChurnDetector {
	return &IPChurnDetector{
		maxIPs: maxIPs,
		window: window,
	}
}

// Check records the (steamID, ip) pair and returns a Detection if the SteamID
// has been seen from more than maxIPs distinct IPs within window.
// steamID=0 is always ignored.
func (d *IPChurnDetector) Check(steamID uint64, ip net.IP) *Detection {
	if d.maxIPs <= 0 || steamID == 0 || ip == nil {
		return nil
	}

	ipStr := ip.String()
	now := time.Now()
	cutoff := now.Add(-d.window)

	val, _ := d.state.LoadOrStore(steamID, &churnRecord{ips: make(map[string]time.Time)})
	record := val.(*churnRecord)

	record.mu.Lock()
	defer record.mu.Unlock()

	// Prune IPs outside the window
	for k, ts := range record.ips {
		if ts.Before(cutoff) {
			delete(record.ips, k)
		}
	}

	record.ips[ipStr] = now

	if len(record.ips) > d.maxIPs {
		return &Detection{
			Heuristic: "ip_churn",
			IP:        ip,
			SteamID:   steamID,
			Detail:    fmt.Sprintf("%d distinct IPs in %v (max: %d)", len(record.ips), d.window, d.maxIPs),
		}
	}

	return nil
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
cd waf && go test ./internal/detect/ -run TestIPChurnDetector -v
```
Expected: all 6 tests PASS

- [ ] **Step 5: Commit**

```bash
cd waf && git add internal/detect/ipchurn.go internal/detect/ipchurn_test.go
git commit -m "feat(waf): per-SteamID IP churn detector"
```

---

## Task 5: BurstDetector

The existing `FloodDetector` uses a 1-second window. Attackers calibrate just under it. A sliding 30-second window catches slow-drip patterns that evade the 1-second check.

**Files:**
- Create: `waf/internal/detect/burst.go`
- Create: `waf/internal/detect/burst_test.go`

- [ ] **Step 1: Write failing test**

```go
// waf/internal/detect/burst_test.go
package detect

import (
	"net"
	"testing"
	"time"
)

func TestBurstDetector_BelowThreshold(t *testing.T) {
	d := NewBurstDetector(time.Second, 10)
	ip := net.ParseIP("20.0.0.1")
	for i := 0; i < 10; i++ {
		if d.Check(ip) {
			t.Fatalf("should not trigger at packet %d (below threshold 10)", i+1)
		}
	}
}

func TestBurstDetector_ExceedsThreshold(t *testing.T) {
	d := NewBurstDetector(time.Second, 5)
	ip := net.ParseIP("20.0.0.2")
	triggered := false
	for i := 0; i < 10; i++ {
		if d.Check(ip) {
			triggered = true
			break
		}
	}
	if !triggered {
		t.Fatal("expected burst detection after exceeding threshold")
	}
}

func TestBurstDetector_WindowExpiry(t *testing.T) {
	d := NewBurstDetector(100*time.Millisecond, 3)
	ip := net.ParseIP("20.0.0.3")
	// Fill the window
	d.Check(ip)
	d.Check(ip)
	d.Check(ip)
	time.Sleep(150 * time.Millisecond) // window slides past all entries
	// New window: count resets
	if d.Check(ip) {
		t.Fatal("should not trigger after window slides past old timestamps")
	}
}

func TestBurstDetector_Disabled(t *testing.T) {
	d := NewBurstDetector(time.Second, 0) // maxPackets=0 disables
	ip := net.ParseIP("20.0.0.4")
	for i := 0; i < 100; i++ {
		if d.Check(ip) {
			t.Fatal("should not trigger when disabled")
		}
	}
}

func TestBurstDetector_NilIP(t *testing.T) {
	d := NewBurstDetector(time.Second, 5)
	if d.Check(nil) {
		t.Fatal("nil IP should not trigger")
	}
}

func TestBurstDetector_IndependentIPs(t *testing.T) {
	d := NewBurstDetector(time.Second, 3)
	ip1 := net.ParseIP("20.0.1.1")
	ip2 := net.ParseIP("20.0.1.2")
	// Exhaust ip1
	for i := 0; i < 5; i++ {
		d.Check(ip1)
	}
	// ip2 should have own bucket
	if d.Check(ip2) {
		t.Fatal("ip2 should not be affected by ip1's burst")
	}
}
```

- [ ] **Step 2: Run test to confirm it fails**

```bash
cd waf && go test ./internal/detect/ -run TestBurstDetector -v
```

- [ ] **Step 3: Write implementation**

```go
// waf/internal/detect/burst.go
package detect

import (
	"net"
	"sync"
	"time"
)

// BurstDetector uses a true sliding window over a longer horizon to detect
// slow-drip flood patterns that stay just under the 1-second FloodDetector
// threshold by spacing packets a few milliseconds apart.
type BurstDetector struct {
	window     time.Duration
	maxPackets int
	buckets    sync.Map // string(ip) → *slidingBucket
}

type slidingBucket struct {
	timestamps []time.Time
	mu         sync.Mutex
}

// NewBurstDetector creates a BurstDetector. Set maxPackets=0 to disable.
func NewBurstDetector(window time.Duration, maxPackets int) *BurstDetector {
	return &BurstDetector{
		window:     window,
		maxPackets: maxPackets,
	}
}

// Check records a packet from ip and returns true if the sliding window count
// exceeds maxPackets.
func (b *BurstDetector) Check(ip net.IP) bool {
	if b.maxPackets <= 0 || ip == nil {
		return false
	}

	now := time.Now()
	cutoff := now.Add(-b.window)

	val, _ := b.buckets.LoadOrStore(ip.String(), &slidingBucket{})
	sw := val.(*slidingBucket)

	sw.mu.Lock()
	defer sw.mu.Unlock()

	// Prune timestamps outside the window
	kept := sw.timestamps[:0]
	for _, ts := range sw.timestamps {
		if ts.After(cutoff) {
			kept = append(kept, ts)
		}
	}
	sw.timestamps = append(kept, now)

	return len(sw.timestamps) > b.maxPackets
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
cd waf && go test ./internal/detect/ -run TestBurstDetector -v
```
Expected: all 6 tests PASS

- [ ] **Step 5: Commit**

```bash
cd waf && git add internal/detect/burst.go internal/detect/burst_test.go
git commit -m "feat(waf): sliding-window burst detector"
```

---

## Task 6: AmplificationGuard + Fix Response Forwarding

The query port (28017) responds to tiny packets with much larger responses — a classic UDP amplification vector. We track the outbound/inbound byte ratio per source IP.

**This task also fixes `readUpstreamResponses` in `proxy/udp.go` which currently discards all upstream responses instead of forwarding them back to clients.** The fix requires storing the listener conn on the `UDPProxy` struct.

**Files:**
- Create: `waf/internal/detect/amplification.go`
- Create: `waf/internal/detect/amplification_test.go`
- Modify: `waf/internal/proxy/udp.go` (fix response forwarding, wire amplification)

- [ ] **Step 1: Write failing test for AmplificationGuard**

```go
// waf/internal/detect/amplification_test.go
package detect

import (
	"net"
	"testing"
)

func TestAmplificationGuard_BelowRatio(t *testing.T) {
	g := NewAmplificationGuard(10.0, 3)
	ip := net.ParseIP("30.0.0.1")
	// 100 bytes in, 500 bytes out = 5x ratio (below 10x)
	g.RecordRequest(ip, 100)
	g.RecordRequest(ip, 100)
	g.RecordRequest(ip, 100)
	g.RecordResponse(ip, 500)
	if g.IsAmplifying(ip) {
		t.Fatal("5x ratio should not trigger (threshold 10x)")
	}
}

func TestAmplificationGuard_ExceedsRatio(t *testing.T) {
	g := NewAmplificationGuard(5.0, 3)
	ip := net.ParseIP("30.0.0.2")
	// 30 bytes in, 600 bytes out = 20x ratio
	g.RecordRequest(ip, 10)
	g.RecordRequest(ip, 10)
	g.RecordRequest(ip, 10)
	g.RecordResponse(ip, 600)
	if !g.IsAmplifying(ip) {
		t.Fatal("20x ratio should trigger (threshold 5x)")
	}
}

func TestAmplificationGuard_MinRequestsNotMet(t *testing.T) {
	g := NewAmplificationGuard(5.0, 10) // requires 10 requests
	ip := net.ParseIP("30.0.0.3")
	// Only 2 requests even though ratio is enormous
	g.RecordRequest(ip, 1)
	g.RecordRequest(ip, 1)
	g.RecordResponse(ip, 10000)
	if g.IsAmplifying(ip) {
		t.Fatal("should not trigger before minRequests threshold met")
	}
}

func TestAmplificationGuard_NoRequests(t *testing.T) {
	g := NewAmplificationGuard(5.0, 3)
	ip := net.ParseIP("30.0.0.4")
	if g.IsAmplifying(ip) {
		t.Fatal("unknown IP should not trigger")
	}
}

func TestAmplificationGuard_Disabled(t *testing.T) {
	g := NewAmplificationGuard(0, 1) // maxRatio=0 disables
	ip := net.ParseIP("30.0.0.5")
	g.RecordRequest(ip, 1)
	g.RecordResponse(ip, 1000000)
	if g.IsAmplifying(ip) {
		t.Fatal("should not trigger when disabled")
	}
}

func TestAmplificationGuard_NilIP(t *testing.T) {
	g := NewAmplificationGuard(5.0, 1)
	// Should not panic
	g.RecordRequest(nil, 100)
	g.RecordResponse(nil, 1000)
	if g.IsAmplifying(nil) {
		t.Fatal("nil IP should not trigger")
	}
}
```

- [ ] **Step 2: Run test to confirm it fails**

```bash
cd waf && go test ./internal/detect/ -run TestAmplificationGuard -v
```

- [ ] **Step 3: Write AmplificationGuard implementation**

```go
// waf/internal/detect/amplification.go
package detect

import (
	"net"
	"sync"
	"sync/atomic"
)

// AmplificationGuard detects and prevents UDP amplification attacks by tracking
// the outbound/inbound byte ratio per source IP on the query port (28017).
// When ratio exceeds the threshold the server is being used as a DDoS relay.
type AmplificationGuard struct {
	maxRatio    float64
	minRequests int64
	state       sync.Map // string(ip) → *ampRecord
}

type ampRecord struct {
	inBytes  atomic.Int64
	outBytes atomic.Int64
	requests atomic.Int64
}

// NewAmplificationGuard creates an AmplificationGuard. Set maxRatio=0 to disable.
func NewAmplificationGuard(maxRatio float64, minRequests int) *AmplificationGuard {
	return &AmplificationGuard{
		maxRatio:    maxRatio,
		minRequests: int64(minRequests),
	}
}

// RecordRequest records an inbound query packet of size bytes from ip.
func (a *AmplificationGuard) RecordRequest(ip net.IP, bytes int) {
	if ip == nil {
		return
	}
	val, _ := a.state.LoadOrStore(ip.String(), &ampRecord{})
	r := val.(*ampRecord)
	r.inBytes.Add(int64(bytes))
	r.requests.Add(1)
}

// RecordResponse records an outbound response of size bytes to ip.
func (a *AmplificationGuard) RecordResponse(ip net.IP, bytes int) {
	if ip == nil {
		return
	}
	val, _ := a.state.LoadOrStore(ip.String(), &ampRecord{})
	r := val.(*ampRecord)
	r.outBytes.Add(int64(bytes))
}

// IsAmplifying returns true if ip's outbound/inbound ratio exceeds maxRatio
// and at least minRequests have been observed.
func (a *AmplificationGuard) IsAmplifying(ip net.IP) bool {
	if a.maxRatio <= 0 || ip == nil {
		return false
	}
	val, ok := a.state.Load(ip.String())
	if !ok {
		return false
	}
	r := val.(*ampRecord)
	if r.requests.Load() < a.minRequests {
		return false
	}
	in := r.inBytes.Load()
	if in == 0 {
		return false
	}
	ratio := float64(r.outBytes.Load()) / float64(in)
	return ratio > a.maxRatio
}
```

- [ ] **Step 4: Fix response forwarding in `proxy/udp.go`**

The `UDPProxy` struct needs to store the listener conn and a reference to the amplification guard. Replace the struct definition and `Start` + `readUpstreamResponses` methods:

```go
// In proxy/udp.go — replace UDPProxy struct
type UDPProxy struct {
	listenAddr   string
	upstreamAddr string
	pipeline     *Pipeline
	port         int
	connMap      sync.Map        // key: string(srcAddr) → *net.UDPConn
	listener     *net.UDPConn    // stored so readUpstreamResponses can write back
}
```

In `Start()`, store the conn before the read loop:

```go
// After: conn, err := net.ListenUDP("udp", udpAddr)
// Add:
u.listener = conn
```

Replace `readUpstreamResponses`:

```go
func (u *UDPProxy) readUpstreamResponses(dstAddr *net.UDPAddr, upConn *net.UDPConn, portStr string) {
	buffer := make([]byte, 65535)
	for {
		n, err := upConn.Read(buffer)
		if err != nil {
			upConn.Close()
			u.connMap.Delete(dstAddr.String())
			return
		}

		payload := make([]byte, n)
		copy(payload, buffer[:n])

		// Forward response back to client via the original listener
		if u.listener != nil {
			if _, werr := u.listener.WriteToUDP(payload, dstAddr); werr != nil {
				log.Printf("[UDP] write response to client %s: %v", dstAddr, werr)
			}
		}

		// Track response size for amplification detection
		if u.pipeline.Amplify != nil {
			u.pipeline.Amplify.RecordResponse(dstAddr.IP, n)
		}

		metrics.PacketsTotal.WithLabelValues(portStr, "response").Inc()
	}
}
```

- [ ] **Step 5: Run all detect tests**

```bash
cd waf && go test ./internal/detect/ -v
```
Expected: all tests PASS

- [ ] **Step 6: Run proxy build check**

```bash
cd waf && go build ./internal/proxy/
```
Expected: compiles without error

- [ ] **Step 7: Commit**

```bash
cd waf && git add internal/detect/amplification.go internal/detect/amplification_test.go internal/proxy/udp.go
git commit -m "feat(waf): amplification guard + fix UDP response forwarding"
```

---

## Task 7: GeoVelocityDetector

A SteamID appearing from geographically distant IPs within an implausibly short window indicates a botnet or credential-shared account. Uses MaxMind GeoLite2 via an interface for testability.

**Files:**
- Create: `waf/internal/detect/geovelocity.go`
- Create: `waf/internal/detect/geovelocity_test.go`
- Modify: `waf/go.mod` + `waf/go.sum`

- [ ] **Step 1: Add geoip2 dependency**

```bash
cd waf && go get github.com/oschwald/geoip2-golang@v1.11.0
```
Expected: `go.mod` and `go.sum` updated

- [ ] **Step 2: Write failing test**

```go
// waf/internal/detect/geovelocity_test.go
package detect

import (
	"net"
	"testing"
	"time"
)

// stubGeoLooker is a test double for geoLooker that returns preconfigured
// lat/lon pairs by IP string.
type stubGeoLooker struct {
	lookup map[string][2]float64 // ip string → [lat, lon]
}

func (s *stubGeoLooker) LookupLatLon(ip net.IP) (float64, float64, bool) {
	coords, ok := s.lookup[ip.String()]
	return coords[0], coords[1], ok
}

func TestGeoVelocityDetector_FirstObservation(t *testing.T) {
	stub := &stubGeoLooker{lookup: map[string][2]float64{
		"1.0.0.1": {40.7128, -74.0060}, // New York
	}}
	d := newGeoVelocityWithLooker(stub, 1000.0)
	ip := net.ParseIP("1.0.0.1")
	// First observation — no prior point to compare against
	if d.Check(ip, 76561198000000020) != nil {
		t.Fatal("first observation should never trigger")
	}
}

func TestGeoVelocityDetector_SameIP(t *testing.T) {
	stub := &stubGeoLooker{lookup: map[string][2]float64{
		"1.0.0.2": {40.7128, -74.0060},
	}}
	d := newGeoVelocityWithLooker(stub, 1000.0)
	ip := net.ParseIP("1.0.0.2")
	const steamID = uint64(76561198000000021)
	d.Check(ip, steamID)
	// Same IP again — velocity = 0, never triggers
	if d.Check(ip, steamID) != nil {
		t.Fatal("same IP repeated should not trigger")
	}
}

func TestGeoVelocityDetector_ImpossibleTravel(t *testing.T) {
	ipNY := net.ParseIP("1.0.0.3") // New York
	ipTK := net.ParseIP("1.0.0.4") // Tokyo
	stub := &stubGeoLooker{lookup: map[string][2]float64{
		"1.0.0.3": {40.7128, -74.0060},    // New York ~40°N, 74°W
		"1.0.0.4": {35.6762, 139.6503},    // Tokyo ~35°N, 139°E
	}}
	d := newGeoVelocityWithLooker(stub, 1000.0) // max 1000 km/h
	const steamID = uint64(76561198000000022)
	d.Check(ipNY, steamID) // record NY

	// Fake 1 second elapsed then check Tokyo — ~10,838 km / (1/3600 h) = 39M km/h
	// We can't easily fake time; instead use a very low speed threshold
	d2 := newGeoVelocityWithLooker(stub, 1.0) // max 1 km/h — anything triggers
	d2.Check(ipNY, steamID)
	det := d2.Check(ipTK, steamID)
	if det == nil {
		t.Fatal("expected geo_velocity detection for NY→Tokyo with 1 km/h threshold")
	}
	if det.Heuristic != "geo_velocity" {
		t.Fatalf("expected heuristic 'geo_velocity', got %q", det.Heuristic)
	}
}

func TestGeoVelocityDetector_DisabledWhenNoLooker(t *testing.T) {
	d := newGeoVelocityWithLooker(nil, 1000.0)
	ip := net.ParseIP("1.0.0.5")
	if d.Check(ip, 76561198000000023) != nil {
		t.Fatal("nil looker should disable detection")
	}
}

func TestGeoVelocityDetector_UnknownIP(t *testing.T) {
	stub := &stubGeoLooker{lookup: map[string][2]float64{}} // empty
	d := newGeoVelocityWithLooker(stub, 1000.0)
	ip := net.ParseIP("192.168.1.1") // private IP — not in GeoIP DB
	d.Check(ip, 76561198000000024)
	if d.Check(ip, 76561198000000024) != nil {
		t.Fatal("unknown IP should not trigger")
	}
}

func TestHaversineKm_SamePoint(t *testing.T) {
	km := haversineKm(40.7128, -74.0060, 40.7128, -74.0060)
	if km > 0.001 {
		t.Fatalf("same point should be ~0 km, got %.4f", km)
	}
}

func TestHaversineKm_NYToLondon(t *testing.T) {
	// NY to London is ~5,570 km
	km := haversineKm(40.7128, -74.0060, 51.5074, -0.1278)
	if km < 5500 || km > 5700 {
		t.Fatalf("NY→London should be ~5570 km, got %.1f", km)
	}
}

// newGeoVelocityWithLooker constructs a GeoVelocityDetector using a custom looker (for testing).
// This constructor is package-private and used only in tests.
func newGeoVelocityWithLooker(looker geoLooker, maxSpeedKmH float64) *GeoVelocityDetector {
	return &GeoVelocityDetector{
		maxSpeedKmH: maxSpeedKmH,
		looker:      looker,
	}
}
```

- [ ] **Step 3: Run test to confirm it fails**

```bash
cd waf && go test ./internal/detect/ -run "TestGeoVelocity|TestHaversine" -v
```

- [ ] **Step 4: Write implementation**

```go
// waf/internal/detect/geovelocity.go
package detect

import (
	"fmt"
	"math"
	"net"
	"sync"
	"time"

	"github.com/oschwald/geoip2-golang"
)

const earthRadiusKm = 6371.0

// geoLooker is the interface for geo-IP lookup, enabling test doubles.
type geoLooker interface {
	LookupLatLon(ip net.IP) (lat, lon float64, ok bool)
}

// geoip2Looker wraps a MaxMind GeoLite2 reader.
type geoip2Looker struct {
	db *geoip2.Reader
}

func (g *geoip2Looker) LookupLatLon(ip net.IP) (float64, float64, bool) {
	rec, err := g.db.City(ip)
	if err != nil || (rec.Location.Latitude == 0 && rec.Location.Longitude == 0) {
		return 0, 0, false
	}
	return rec.Location.Latitude, rec.Location.Longitude, true
}

// GeoVelocityDetector flags a SteamID appearing from geographically impossible
// locations — indicating a credential-shared botnet or VPN-hopping that covers
// more distance per hour than any real-world travel allows.
type GeoVelocityDetector struct {
	maxSpeedKmH float64
	looker      geoLooker
	last        sync.Map // uint64 → *geoPoint
}

type geoPoint struct {
	lat  float64
	lon  float64
	seen time.Time
	ip   string // string for comparison; avoids aliasing
	mu   sync.Mutex
}

// NewGeoVelocityDetector opens a MaxMind GeoLite2-City database at dbPath.
// Returns a disabled (no-op) detector if dbPath is empty — graceful degradation.
func NewGeoVelocityDetector(dbPath string, maxSpeedKmH float64) (*GeoVelocityDetector, error) {
	if dbPath == "" {
		return &GeoVelocityDetector{maxSpeedKmH: maxSpeedKmH}, nil
	}
	db, err := geoip2.Open(dbPath)
	if err != nil {
		return nil, fmt.Errorf("open GeoLite2 DB %s: %w", dbPath, err)
	}
	return &GeoVelocityDetector{
		maxSpeedKmH: maxSpeedKmH,
		looker:      &geoip2Looker{db: db},
	}, nil
}

// Close releases the GeoIP2 file handle if open.
func (g *GeoVelocityDetector) Close() {
	if l, ok := g.looker.(*geoip2Looker); ok && l.db != nil {
		l.db.Close()
	}
}

// Check looks up ip's geolocation and compares it to the last known location
// for steamID. Returns a Detection if the implied travel speed exceeds maxSpeedKmH.
// Returns nil if: looker is nil, steamID=0, IP not in DB, or first observation.
func (g *GeoVelocityDetector) Check(ip net.IP, steamID uint64) *Detection {
	if g.looker == nil || g.maxSpeedKmH <= 0 || steamID == 0 || ip == nil {
		return nil
	}

	lat, lon, ok := g.looker.LookupLatLon(ip)
	if !ok {
		return nil
	}

	ipStr := ip.String()
	now := time.Now()

	val, loaded := g.last.LoadOrStore(steamID, &geoPoint{
		lat:  lat,
		lon:  lon,
		seen: now,
		ip:   ipStr,
	})
	if !loaded {
		return nil // first observation
	}

	pt := val.(*geoPoint)
	pt.mu.Lock()
	defer pt.mu.Unlock()

	prevLat, prevLon, prevSeen, prevIP := pt.lat, pt.lon, pt.seen, pt.ip

	// Update to latest observation
	pt.lat = lat
	pt.lon = lon
	pt.seen = now
	pt.ip = ipStr

	if prevIP == ipStr {
		return nil // same IP, zero distance
	}

	distKm := haversineKm(prevLat, prevLon, lat, lon)
	elapsedH := now.Sub(prevSeen).Hours()
	if elapsedH <= 0 {
		return nil
	}

	speedKmH := distKm / elapsedH
	if speedKmH <= g.maxSpeedKmH {
		return nil
	}

	return &Detection{
		Heuristic: "geo_velocity",
		IP:        ip,
		SteamID:   steamID,
		Detail: fmt.Sprintf("%.0f km/h implied (%.0f km in %.1f min) prev=%s now=%s",
			speedKmH, distKm, elapsedH*60, prevIP, ipStr),
	}
}

// haversineKm returns the great-circle distance in km between two lat/lon points.
func haversineKm(lat1, lon1, lat2, lon2 float64) float64 {
	dLat := (lat2 - lat1) * math.Pi / 180
	dLon := (lon2 - lon1) * math.Pi / 180
	a := math.Sin(dLat/2)*math.Sin(dLat/2) +
		math.Cos(lat1*math.Pi/180)*math.Cos(lat2*math.Pi/180)*
			math.Sin(dLon/2)*math.Sin(dLon/2)
	c := 2 * math.Atan2(math.Sqrt(a), math.Sqrt(1-a))
	return earthRadiusKm * c
}
```

- [ ] **Step 5: Run tests to confirm they pass**

```bash
cd waf && go test ./internal/detect/ -run "TestGeoVelocity|TestHaversine" -v
```
Expected: all 8 tests PASS

- [ ] **Step 6: Run full detect suite**

```bash
cd waf && go test ./internal/detect/ -v
```
Expected: all tests PASS

- [ ] **Step 7: Commit**

```bash
cd waf && git add internal/detect/geovelocity.go internal/detect/geovelocity_test.go go.mod go.sum
git commit -m "feat(waf): geo-velocity impossible travel detector"
```

---

## Task 8: Wire Detectors into Pipeline, UDP Proxy, and Metrics

All 7 detectors are integrated into `proxy.Pipeline`, called in `proxy/udp.go`'s `inspectAndForward`, and new Prometheus metrics are registered.

**Files:**
- Modify: `waf/internal/proxy/pipeline.go`
- Modify: `waf/internal/proxy/udp.go`
- Modify: `waf/internal/metrics/metrics.go`

- [ ] **Step 1: Update `pipeline.go` to include all new detectors**

Replace the entire file:

```go
// waf/internal/proxy/pipeline.go
package proxy

import (
	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/rules"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

// Pipeline holds all dependencies needed for packet inspection.
type Pipeline struct {
	Store       *state.Store
	Mapper      *detect.Mapper
	Limiter     *detect.RateLimiter
	Flood       *detect.FloodDetector
	Patterns    *detect.PatternDetector
	Rules       *rules.Engine
	Reconnect   *detect.ReconnectDetector
	Handshake   *detect.HandshakeTracker
	Entropy     *detect.EntropyDetector
	IPChurn     *detect.IPChurnDetector
	Burst       *detect.BurstDetector
	Amplify     *detect.AmplificationGuard
	GeoVelocity *detect.GeoVelocityDetector
}
```

- [ ] **Step 2: Add new metrics to `metrics.go`**

Append to the `var (` block in `waf/internal/metrics/metrics.go`:

```go
	ReconnectStorms = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_reconnect_storms_total",
			Help: "Reconnect storm events detected per SteamID",
		},
		[]string{"steam64"},
	)

	IncompleteHandshakes = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_incomplete_handshakes_total",
			Help: "Incomplete handshake flood events by IP",
		},
		[]string{"ip"},
	)

	EntropyDrops = promauto.NewCounter(
		prometheus.CounterOpts{
			Name: "waf_entropy_drops_total",
			Help: "Packets dropped due to high payload entropy",
		},
	)

	IPChurnEvents = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_ip_churn_events_total",
			Help: "IP churn detections per SteamID",
		},
		[]string{"steam64"},
	)

	BurstDrops = promauto.NewCounter(
		prometheus.CounterOpts{
			Name: "waf_burst_drops_total",
			Help: "Packets dropped due to sliding-window burst detection",
		},
	)

	AmplificationBlocks = promauto.NewCounter(
		prometheus.CounterOpts{
			Name: "waf_amplification_blocks_total",
			Help: "IPs blocked for query amplification abuse",
		},
	)

	GeoVelocityEvents = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_geo_velocity_events_total",
			Help: "Geographically impossible travel events per SteamID",
		},
		[]string{"steam64"},
	)
```

- [ ] **Step 3: Wire new detectors into `inspectAndForward` in `udp.go`**

Insert the following steps into `inspectAndForward` in `proxy/udp.go` **after Step 8 (Flood detection, line ~148) and before Step 10 (Pattern heuristics)**:

```go
	// Step 8a: Burst detection (sliding window — catches slow-drip floods)
	if u.pipeline.Burst != nil && u.pipeline.Burst.Check(srcIP) {
		metrics.BurstDrops.Inc()
		metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
		log.Printf("[UDP] Drop burst: IP=%s", srcIP.String())
		return
	}

	// Step 8b: Incomplete handshake flood — record packet, flag flood IPs
	if u.pipeline.Handshake != nil {
		if found {
			u.pipeline.Handshake.RecordCompletion(srcIP)
		} else if u.pipeline.Handshake.RecordPacket(srcIP) {
			metrics.IncompleteHandshakes.WithLabelValues(srcIP.String()).Inc()
			metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
			log.Printf("[UDP] Drop incomplete handshake flood: IP=%s", srcIP.String())
			return
		}
	}

	// Step 8c: Reconnect storm — only on newly-seen SteamIDs in this packet
	if u.pipeline.Reconnect != nil && found {
		if u.pipeline.Reconnect.RecordAuth(srcIP, steamID) {
			metrics.ReconnectStorms.WithLabelValues(fmt.Sprintf("%d", steamID)).Inc()
			metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
			log.Printf("[UDP] Drop reconnect storm: SteamID=%d IP=%s", steamID, srcIP.String())
			return
		}
	}

	// Step 8d: IP churn — SteamID appearing from too many distinct IPs
	if u.pipeline.IPChurn != nil && found {
		if det := u.pipeline.IPChurn.Check(steamID, srcIP); det != nil {
			metrics.IPChurnEvents.WithLabelValues(fmt.Sprintf("%d", steamID)).Inc()
			log.Printf("[UDP] IP churn: %s", det.Detail)
			// Log only — don't drop; churn is evidence, not proof
		}
	}

	// Step 8e: Amplification guard (query port only — 28017)
	if u.pipeline.Amplify != nil {
		u.pipeline.Amplify.RecordRequest(srcIP, len(payload))
		if u.pipeline.Amplify.IsAmplifying(srcIP) {
			metrics.AmplificationBlocks.Inc()
			metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
			log.Printf("[UDP] Drop amplification abuse: IP=%s", srcIP.String())
			u.pipeline.Store.BlockIP(srcIP, 30*time.Minute, "AMPLIFICATION")
			return
		}
	}
```

Also add inside the pattern heuristics loop (Step 10) after existing detections, still before Step 11:

```go
	// Step 10a: Entropy detection
	if u.pipeline.Entropy != nil {
		if det := u.pipeline.Entropy.Check(srcIP, steamID, payload); det != nil {
			metrics.EntropyDrops.Inc()
			metrics.DetectionEvents.WithLabelValues(det.Heuristic).Inc()
			metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
			log.Printf("[UDP] Drop high entropy: IP=%s SteamID=%d Detail=%s", srcIP, steamID, det.Detail)
			return
		}
	}

	// Step 10b: Geo-velocity detection
	if u.pipeline.GeoVelocity != nil && found {
		if det := u.pipeline.GeoVelocity.Check(srcIP, steamID); det != nil {
			metrics.GeoVelocityEvents.WithLabelValues(fmt.Sprintf("%d", steamID)).Inc()
			metrics.DetectionEvents.WithLabelValues(det.Heuristic).Inc()
			log.Printf("[UDP] Geo velocity: %s", det.Detail)
			// Log only — geo velocity is a strong signal but not a hard block
		}
	}
```

Add `"fmt"` to imports in `udp.go` if not already present.

- [ ] **Step 4: Build the proxy package**

```bash
cd waf && go build ./...
```
Expected: compiles without error

- [ ] **Step 5: Run all tests**

```bash
cd waf && go test ./...
```
Expected: all tests PASS

- [ ] **Step 6: Commit**

```bash
cd waf && git add internal/proxy/pipeline.go internal/proxy/udp.go internal/metrics/metrics.go
git commit -m "feat(waf): wire 7 new detectors into inspection pipeline"
```

---

## Task 9: Environment Variables in `main.go`

Wire all new detector env vars so operators can tune or disable each detector without rebuilding.

**Files:**
- Modify: `waf/cmd/waf/main.go`

- [ ] **Step 1: Add env var parsing and detector construction**

In `main.go`, after the existing env var declarations (after line ~50), add:

```go
	// ── New detectors ─────────────────────────────────────────────────────
	reconnectMaxStr     := getEnv("WAF_RECONNECT_MAX_PER_WINDOW", "10")
	reconnectWindowStr  := getEnv("WAF_RECONNECT_WINDOW", "1m")
	handshakeMaxPending := getEnvInt("WAF_HANDSHAKE_MAX_PENDING", 20)
	handshakeTimeoutStr := getEnv("WAF_HANDSHAKE_TIMEOUT", "10s")
	entropyThreshold    := getEnvFloat("WAF_ENTROPY_THRESHOLD", 7.5)
	entropyMinSize      := getEnvInt("WAF_ENTROPY_MIN_SIZE", 64)
	ipChurnMaxIPs       := getEnvInt("WAF_IP_CHURN_MAX_IPS", 5)
	ipChurnWindowStr    := getEnv("WAF_IP_CHURN_WINDOW", "5m")
	burstWindowStr      := getEnv("WAF_BURST_WINDOW", "30s")
	burstMaxPackets     := getEnvInt("WAF_BURST_MAX_PACKETS", 3000)
	amplifyMaxRatio     := getEnvFloat("WAF_AMPLIFY_MAX_RATIO", 10.0)
	amplifyMinRequests  := getEnvInt("WAF_AMPLIFY_MIN_REQUESTS", 5)
	geoDBPath           := getEnv("WAF_GEO_DB_PATH", "")
	geoMaxSpeedKmH      := getEnvFloat("WAF_GEO_MAX_SPEED_KMH", 1000.0)
```

Parse durations (add after existing `cfgPollInterval` parsing):

```go
	reconnectMax, err := strconv.Atoi(reconnectMaxStr)
	if err != nil {
		log.Fatalf("Invalid WAF_RECONNECT_MAX_PER_WINDOW: %v", err)
	}
	reconnectWindow, err := time.ParseDuration(reconnectWindowStr)
	if err != nil {
		log.Fatalf("Invalid WAF_RECONNECT_WINDOW: %v", err)
	}
	handshakeTimeout, err := time.ParseDuration(handshakeTimeoutStr)
	if err != nil {
		log.Fatalf("Invalid WAF_HANDSHAKE_TIMEOUT: %v", err)
	}
	ipChurnWindow, err := time.ParseDuration(ipChurnWindowStr)
	if err != nil {
		log.Fatalf("Invalid WAF_IP_CHURN_WINDOW: %v", err)
	}
	burstWindow, err := time.ParseDuration(burstWindowStr)
	if err != nil {
		log.Fatalf("Invalid WAF_BURST_WINDOW: %v", err)
	}
```

Construct detectors (add after existing detector construction, before pipeline creation):

```go
	reconnectDetector  := detect.NewReconnectDetector(reconnectMax, reconnectWindow)
	handshakeTracker   := detect.NewHandshakeTracker(handshakeMaxPending, handshakeTimeout)
	entropyDetector    := detect.NewEntropyDetector(entropyThreshold, entropyMinSize)
	ipChurnDetector    := detect.NewIPChurnDetector(ipChurnMaxIPs, ipChurnWindow)
	burstDetector      := detect.NewBurstDetector(burstWindow, burstMaxPackets)
	amplifyGuard       := detect.NewAmplificationGuard(amplifyMaxRatio, amplifyMinRequests)

	geoDetector, err := detect.NewGeoVelocityDetector(geoDBPath, geoMaxSpeedKmH)
	if err != nil {
		log.Fatalf("GeoVelocity detector: %v", err)
	}
	defer geoDetector.Close()
```

Update the pipeline construction to include new detectors:

```go
	pipeline := &proxy.Pipeline{
		Store:       store,
		Mapper:      mapper,
		Limiter:     limiter,
		Flood:       flood,
		Patterns:    patterns,
		Rules:       rulesEngine,
		Reconnect:   reconnectDetector,
		Handshake:   handshakeTracker,
		Entropy:     entropyDetector,
		IPChurn:     ipChurnDetector,
		Burst:       burstDetector,
		Amplify:     amplifyGuard,
		GeoVelocity: geoDetector,
	}
```

- [ ] **Step 2: Build the full binary**

```bash
cd waf && go build ./cmd/waf/
```
Expected: binary produced without error

- [ ] **Step 3: Run all tests**

```bash
cd waf && go test ./...
```
Expected: all tests PASS

- [ ] **Step 4: Commit**

```bash
cd waf && git add cmd/waf/main.go
git commit -m "feat(waf): env vars and wiring for 7 new detectors"
```

---

## Task 10: Update Docs

Update both `waf/README.md` and `docs/waf.md` to document all 7 new protections, new env vars, and new Prometheus metrics.

**Files:**
- Modify: `waf/README.md`
- Modify: `docs/waf.md`

- [ ] **Step 1: Update the 12-step pipeline section in both docs**

The pipeline is now a 19-step pipeline. Update the numbered list in both files to include:

```
8a. Burst Detection — sliding 30-second window catches slow-drip floods that evade the 1-second threshold
8b. Incomplete Handshake Flood — IPs that never complete Steam auth flagged as connection-exhaustion probes
8c. Reconnect Storm — SteamID reconnecting too rapidly (crash-exploit reconnect loop)
8d. IP Churn — SteamID seen from N+ distinct IPs in window (VPN-hopper / botnet credential sharing)
8e. Amplification Guard — query port outbound/inbound byte ratio; server being used as DDoS relay
10a. Payload Entropy — high-entropy payload (>7.5 bits) dropped; exploit tools don't spoof valid Rust packets
10b. Geo Velocity — SteamID appearing at physically impossible speed between locations
```

- [ ] **Step 2: Add new env vars table to both docs**

Add these rows to the env var tables:

```
| `WAF_RECONNECT_MAX_PER_WINDOW` | `10` | Max auth handshakes per SteamID in window before storm detection |
| `WAF_RECONNECT_WINDOW` | `1m` | Rolling window for reconnect storm counting |
| `WAF_HANDSHAKE_MAX_PENDING` | `20` | Max incomplete handshakes per IP before flood detection |
| `WAF_HANDSHAKE_TIMEOUT` | `10s` | How long to wait for handshake completion |
| `WAF_ENTROPY_THRESHOLD` | `7.5` | Payload entropy threshold in bits (max 8.0); 0 = disabled |
| `WAF_ENTROPY_MIN_SIZE` | `64` | Minimum payload size in bytes to evaluate entropy |
| `WAF_IP_CHURN_MAX_IPS` | `5` | Max distinct IPs per SteamID in window before flagging |
| `WAF_IP_CHURN_WINDOW` | `5m` | Rolling window for IP churn counting |
| `WAF_BURST_WINDOW` | `30s` | Sliding window duration for burst detection |
| `WAF_BURST_MAX_PACKETS` | `3000` | Max packets in burst window before dropping |
| `WAF_AMPLIFY_MAX_RATIO` | `10.0` | Max outbound/inbound byte ratio before amplification block |
| `WAF_AMPLIFY_MIN_REQUESTS` | `5` | Min requests before amplification ratio is evaluated |
| `WAF_GEO_DB_PATH` | *(empty)* | Path to MaxMind GeoLite2-City.mmdb; empty = disabled |
| `WAF_GEO_MAX_SPEED_KMH` | `1000.0` | Max implied travel speed before geo-velocity alert |
```

- [ ] **Step 3: Add new metrics to the Prometheus table**

```
| `waf_reconnect_storms_total` | Counter | Reconnect storm events by steam64 label |
| `waf_incomplete_handshakes_total` | Counter | Incomplete handshake flood events by ip label |
| `waf_entropy_drops_total` | Counter | Packets dropped for high entropy |
| `waf_ip_churn_events_total` | Counter | IP churn detections by steam64 label |
| `waf_burst_drops_total` | Counter | Packets dropped by sliding-window burst detector |
| `waf_amplification_blocks_total` | Counter | IPs blocked for query amplification abuse |
| `waf_geo_velocity_events_total` | Counter | Geo-velocity impossible travel events by steam64 label |
```

- [ ] **Step 4: Update the "Protections" table in `docs/waf.md`**

Add rows for each new protection, noting vanilla vs. Oxide column.

- [ ] **Step 5: Add GeoLite2 setup note**

In `docs/waf.md`, add a section explaining how to obtain GeoLite2:

```markdown
### GeoVelocity Setup (Optional)

Download the free MaxMind GeoLite2-City database:
1. Register at https://www.maxmind.com/en/geolite2/signup
2. Download `GeoLite2-City.mmdb`
3. Mount it into the sidecar container and set `WAF_GEO_DB_PATH=/path/to/GeoLite2-City.mmdb`

If `WAF_GEO_DB_PATH` is empty (default), geo-velocity detection is silently disabled — no crash, no error.
```

- [ ] **Step 6: Commit**

```bash
cd waf && git add waf/README.md docs/waf.md  # run from repo root
git commit -m "docs(waf): document 7 new vanilla-mode detectors"
```

---

## Self-Review

**Spec coverage check:**
- ✅ Reconnect storm → Task 1 + Task 8 (pipeline step 8c)
- ✅ Incomplete handshake flood → Task 2 + Task 8 (pipeline step 8b)
- ✅ Payload entropy → Task 3 + Task 8 (pipeline step 10a)
- ✅ IP churn → Task 4 + Task 8 (pipeline step 8d)
- ✅ Burst shape → Task 5 + Task 8 (pipeline step 8a)
- ✅ Amplification guard → Task 6 + Task 8 (pipeline step 8e) + response forwarding fix
- ✅ Geo velocity → Task 7 + Task 8 (pipeline step 10b)
- ✅ Docs update → Task 10
- ✅ Metrics → Task 8 Step 2

**Placeholder scan:** No TBD, TODO, or vague steps found. All code is concrete.

**Type consistency check:**
- `detect.NewReconnectDetector(int, time.Duration)` → used in Task 1 and Task 9 ✅
- `detect.NewHandshakeTracker(int, time.Duration)` → used in Task 2 and Task 9 ✅
- `detect.NewEntropyDetector(float64, int)` → used in Task 3 and Task 9 ✅
- `detect.NewIPChurnDetector(int, time.Duration)` → used in Task 4 and Task 9 ✅
- `detect.NewBurstDetector(time.Duration, int)` → used in Task 5 and Task 9 ✅
- `detect.NewAmplificationGuard(float64, int)` → used in Task 6 and Task 9 ✅
- `detect.NewGeoVelocityDetector(string, float64)` returns `(*GeoVelocityDetector, error)` → used in Task 7 and Task 9 ✅
- `geoLooker` interface defined in `geovelocity.go`, `stubGeoLooker` implements it in `geovelocity_test.go` ✅
- `newGeoVelocityWithLooker` helper defined in `geovelocity_test.go` (package-private) ✅
- `Pipeline.Amplify *detect.AmplificationGuard` → set in Task 9, referenced in `udp.go` Task 8 ✅
