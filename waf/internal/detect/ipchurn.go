package detect

import (
	"net"
	"sync"
	"time"
)

// IPChurnDetector detects when a single Steam64 ID connects from many different
// IPs in a short window — a sign of VPN-hopping ban evasion.
type IPChurnDetector struct {
	mode    DetectorMode
	maxIPs  int
	window  time.Duration
	records sync.Map // keyed by uint64 steamID → *churnRecord
}

// churnRecord tracks IP connections for a single steamID.
type churnRecord struct {
	mu  sync.Mutex
	ips map[string]time.Time // ip string → last seen time
}

// NewIPChurnDetector creates a new IP churn detector.
// maxIPs <= 0 disables it (always returns false).
// window specifies the time window for tracking connections.
func NewIPChurnDetector(mode DetectorMode, maxIPs int, window time.Duration) *IPChurnDetector {
	return &IPChurnDetector{
		mode:   mode,
		maxIPs: maxIPs,
		window: window,
	}
}

// Mode returns the detector's current DetectorMode.
func (d *IPChurnDetector) Mode() DetectorMode { return d.mode }

// RecordConnect records a connection from ip for steamID.
// Returns true if this steamID has been seen from > maxIPs distinct IPs within the window.
// steamID == 0 returns false (unauthenticated packets have no Steam identity).
// ip == nil returns false.
func (d *IPChurnDetector) RecordConnect(ip net.IP, steamID uint64) bool {
	if d.mode == ModeOff {
		return false
	}
	// Disabled detector or invalid steamID
	if steamID == 0 || d.maxIPs <= 0 || ip == nil {
		return false
	}

	// Load or create churnRecord for this steamID
	ipStr := ip.String()
	now := time.Now()

	// Load or store a new record for this steamID
	recordIface, _ := d.records.LoadOrStore(steamID, &churnRecord{
		ips: make(map[string]time.Time),
	})
	record := recordIface.(*churnRecord)

	record.mu.Lock()
	defer record.mu.Unlock()

	// Prune IPs last seen before (now - window)
	cutoff := now.Add(-d.window)
	for ipKey, lastSeen := range record.ips {
		if lastSeen.Before(cutoff) {
			delete(record.ips, ipKey)
		}
	}

	// Add/update current IP with now timestamp
	record.ips[ipStr] = now

	// Return true if we now exceed maxIPs distinct IPs
	return len(record.ips) > d.maxIPs
}
