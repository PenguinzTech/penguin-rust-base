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
	mode         DetectorMode
	maxPerWindow int
	window       time.Duration
	sessions     sync.Map // uint64 → *reconnectRecord
}

type reconnectRecord struct {
	timestamps []time.Time
	mu         sync.Mutex
}

// NewReconnectDetector creates a ReconnectDetector. Set maxPerWindow=0 to disable.
func NewReconnectDetector(mode DetectorMode, maxPerWindow int, window time.Duration) *ReconnectDetector {
	return &ReconnectDetector{
		mode:         mode,
		maxPerWindow: maxPerWindow,
		window:       window,
	}
}

// Mode returns the detector's current DetectorMode.
func (r *ReconnectDetector) Mode() DetectorMode { return r.mode }

// RecordAuth records an authentication handshake for steamID.
// Returns true if a reconnect storm is detected (count > maxPerWindow in window).
// steamID=0 is always ignored (pre-auth packets have no SteamID yet).
func (r *ReconnectDetector) RecordAuth(ip net.IP, steamID uint64) bool {
	if r.mode == ModeOff {
		return false
	}
	if r.maxPerWindow <= 0 || steamID == 0 {
		return false
	}

	val, _ := r.sessions.LoadOrStore(steamID, &reconnectRecord{})
	record := val.(*reconnectRecord)

	record.mu.Lock()
	defer record.mu.Unlock()

	now := time.Now()
	cutoff := now.Add(-r.window)

	kept := record.timestamps[:0]
	for _, ts := range record.timestamps {
		if ts.After(cutoff) {
			kept = append(kept, ts)
		}
	}
	record.timestamps = append(kept, now)

	return len(record.timestamps) > r.maxPerWindow
}
