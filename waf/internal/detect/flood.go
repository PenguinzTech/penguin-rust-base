package detect

import (
	"fmt"
	"net"
	"sync"
	"time"
)

// FloodDetector tracks new-connection rates per /24 subnet and per SteamID.
type FloodDetector struct {
	thresholdCPS float64
	windows      sync.Map // key: string → *window
}

type window struct {
	count     int64
	windowEnd time.Time
	mu        sync.Mutex
}

// NewFloodDetector creates a new FloodDetector with the given threshold (connections per second).
func NewFloodDetector(thresholdCPS float64) *FloodDetector {
	return &FloodDetector{
		thresholdCPS: thresholdCPS,
	}
}

// CheckNew records a new connection from ip/steamID.
// Returns true if the /24 subnet or steamID exceeds thresholdCPS.
func (f *FloodDetector) CheckNew(ip net.IP, steamID uint64) bool {
	if f.thresholdCPS <= 0 {
		// Rate limiting disabled
		return false
	}

	// Check /24 subnet (or /48 for IPv6)
	subnetKey := f.getSubnetKey(ip)
	if f.checkWindow(subnetKey) {
		return true
	}

	// Check SteamID if non-zero
	if steamID != 0 {
		steamKey := fmt.Sprintf("steam:%d", steamID)
		if f.checkWindow(steamKey) {
			return true
		}
	}

	return false
}

// getSubnetKey extracts the /24 subnet (IPv4) or /48 prefix (IPv6) from an IP.
func (f *FloodDetector) getSubnetKey(ip net.IP) string {
	if ip == nil {
		return ""
	}

	if ip4 := ip.To4(); ip4 != nil {
		// IPv4: take first 3 octets
		return fmt.Sprintf("%d.%d.%d.0/24", ip4[0], ip4[1], ip4[2])
	}

	// IPv6: take first 6 words (48 bits)
	if ip16 := ip.To16(); ip16 != nil {
		return fmt.Sprintf("%x:%x:%x:0/48", ip16[0:2], ip16[2:4], ip16[4:6])
	}

	return ip.String()
}

// checkWindow checks and updates the connection window for the given key.
// Returns true if the threshold is exceeded in the current second.
func (f *FloodDetector) checkWindow(key string) bool {
	now := time.Now()

	val, _ := f.windows.LoadOrStore(key, &window{
		count:     0,
		windowEnd: now.Add(1 * time.Second),
	})

	w := val.(*window)
	w.mu.Lock()
	defer w.mu.Unlock()

	// If window has expired, reset it
	if now.After(w.windowEnd) {
		w.count = 1
		w.windowEnd = now.Add(1 * time.Second)
		return false
	}

	// Increment and check threshold
	w.count++
	threshold := int64(f.thresholdCPS)
	if threshold <= 0 {
		threshold = 1
	}

	return w.count > threshold
}
