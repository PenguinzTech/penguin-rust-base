package detect

import (
	"net"
	"sync"
	"time"
)

// BurstDetector detects packet bursts: a single IP sending N+ packets within a short time window.
type BurstDetector struct {
	mode     DetectorMode
	maxBurst int
	window   time.Duration
	records  sync.Map // keyed by IP string → *burstRecord
}

type burstRecord struct {
	mu         sync.Mutex
	timestamps []time.Time
}

// NewBurstDetector creates a detector with the given mode.
// maxBurst <= 0 or mode == ModeOff → always returns false.
func NewBurstDetector(mode DetectorMode, maxBurst int, window time.Duration) *BurstDetector {
	return &BurstDetector{
		mode:     mode,
		maxBurst: maxBurst,
		window:   window,
	}
}

// Mode returns the detector's configured mode.
func (b *BurstDetector) Mode() DetectorMode {
	return b.mode
}

// RecordPacket records a packet from ip.
// Returns true if the IP sent > maxBurst packets within the window.
// nil ip returns false.
func (b *BurstDetector) RecordPacket(ip net.IP) bool {
	// Early exits
	if b.mode == ModeOff || b.maxBurst <= 0 || ip == nil {
		return false
	}

	ipStr := ip.String()

	// Load or create burstRecord for this IP
	val, _ := b.records.LoadOrStore(ipStr, &burstRecord{
		timestamps: []time.Time{},
	})

	record := val.(*burstRecord)
	record.mu.Lock()
	defer record.mu.Unlock()

	now := time.Now()

	// Prune timestamps older than (now - window)
	cutoff := now.Add(-b.window)
	var pruned []time.Time
	for _, ts := range record.timestamps {
		if ts.After(cutoff) {
			pruned = append(pruned, ts)
		}
	}
	record.timestamps = pruned

	// Append current timestamp
	record.timestamps = append(record.timestamps, now)

	// Return true if we exceed maxBurst
	return len(record.timestamps) > b.maxBurst
}
