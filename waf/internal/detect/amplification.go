package detect

import (
	"net"
	"sync"
)

// AmplificationGuard detects UDP amplification attacks by tracking the ratio
// of response bytes to request bytes per source IP.
type AmplificationGuard struct {
	mode     DetectorMode
	maxRatio float64
	records  sync.Map // key: string(IP) → *ampRecord
}

type ampRecord struct {
	mu            sync.Mutex
	requestBytes  uint64
	responseBytes uint64
}

// NewAmplificationGuard creates a guard. maxRatio <= 0 or mode == ModeOff → always returns false.
func NewAmplificationGuard(mode DetectorMode, maxRatio float64) *AmplificationGuard {
	return &AmplificationGuard{mode: mode, maxRatio: maxRatio}
}

// Mode returns the detector mode.
func (a *AmplificationGuard) Mode() DetectorMode {
	return a.mode
}

// RecordRequest records inbound bytes from ip.
func (a *AmplificationGuard) RecordRequest(ip net.IP, bytes int) {
	if ip == nil {
		return
	}
	r := a.record(ip.String())
	r.mu.Lock()
	r.requestBytes += uint64(bytes)
	r.mu.Unlock()
}

// RecordResponse records outbound bytes destined for ip.
// Returns true if response/request ratio exceeds maxRatio.
// Returns false if requestBytes == 0 (avoid division by zero) or mode is off.
func (a *AmplificationGuard) RecordResponse(ip net.IP, bytes int) bool {
	if ip == nil {
		return false
	}
	if a.mode == ModeOff || a.maxRatio <= 0 {
		return false
	}
	r := a.record(ip.String())
	r.mu.Lock()
	r.responseBytes += uint64(bytes)
	req := r.requestBytes
	resp := r.responseBytes
	r.mu.Unlock()

	if req == 0 {
		return false
	}
	return float64(resp)/float64(req) > a.maxRatio
}

func (a *AmplificationGuard) record(key string) *ampRecord {
	val, _ := a.records.LoadOrStore(key, &ampRecord{})
	return val.(*ampRecord)
}
