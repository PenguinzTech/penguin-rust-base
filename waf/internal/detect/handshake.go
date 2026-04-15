package detect

import (
	"net"
	"sync"
	"time"
)

// HandshakeTracker detects IPs that repeatedly initiate connections but never
// complete Steam authentication. These are connection-exhaustion flood tools.
type HandshakeTracker struct {
	mode       DetectorMode
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
func NewHandshakeTracker(mode DetectorMode, maxPending int, timeout time.Duration) *HandshakeTracker {
	return &HandshakeTracker{
		mode:       mode,
		maxPending: maxPending,
		timeout:    timeout,
	}
}

// Mode returns the detector's current DetectorMode.
func (h *HandshakeTracker) Mode() DetectorMode { return h.mode }

// RecordPacket records a packet from ip. Returns true if the IP has too many
// incomplete handshakes (flood detected). Thread-safe.
func (h *HandshakeTracker) RecordPacket(ip net.IP) bool {
	if h.mode == ModeOff {
		return false
	}
	if h.maxPending <= 0 || ip == nil {
		return false
	}

	ipStr := ip.String()
	now := time.Now()

	val, loaded := h.pending.LoadOrStore(ipStr, &handshakeRecord{
		attempts:  1,
		firstSeen: now,
	})
	record := val.(*handshakeRecord)
	record.mu.Lock()
	defer record.mu.Unlock()

	if !loaded {
		return false // first packet, count=1, can't trigger yet
	}

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
	}

	record.attempts++
	return record.attempts > h.maxPending
}

// RecordCompletion marks the handshake for ip as complete (SteamID extracted).
// Resets the incomplete counter for this IP.
func (h *HandshakeTracker) RecordCompletion(ip net.IP) {
	if h.mode == ModeOff || ip == nil {
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
