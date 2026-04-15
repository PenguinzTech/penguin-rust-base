package detect

import (
	"net"
	"sync"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

// RCONTracker tracks RCON WebSocket authentication failures per IP.
type RCONTracker struct {
	banAfter int
	failures sync.Map // key: string(ip) → *failureRecord
}

type failureRecord struct {
	count int
	mu    sync.Mutex
}

// NewRCONTracker creates a new RCONTracker.
// banAfter is the number of failures after which an IP is blocked.
func NewRCONTracker(banAfter int) *RCONTracker {
	return &RCONTracker{
		banAfter: banAfter,
	}
}

// RecordFailure increments the failure count for the given IP.
// If count reaches banAfter, the IP is blocked in the store (if store is non-nil).
func (t *RCONTracker) RecordFailure(ip net.IP, store *state.Store) {
	if ip == nil {
		return
	}

	ipStr := ip.String()

	val, _ := t.failures.LoadOrStore(ipStr, &failureRecord{count: 0})
	record := val.(*failureRecord)

	record.mu.Lock()
	record.count++
	count := record.count
	record.mu.Unlock()

	// Block the IP if threshold exceeded
	if count >= t.banAfter && store != nil {
		store.BlockIP(ip, 0, "RCON_AUTH_FAILURE")
	}
}

// IsBlocked returns true if the IP has exceeded its failure threshold.
func (t *RCONTracker) IsBlocked(ip net.IP) bool {
	if ip == nil {
		return false
	}

	val, ok := t.failures.Load(ip.String())
	if !ok {
		return false
	}

	record, ok := val.(*failureRecord)
	if !ok {
		return false
	}

	record.mu.Lock()
	defer record.mu.Unlock()

	return record.count >= t.banAfter
}

// Reset clears the failure count for the given IP.
// Should be called after a successful RCON authentication.
func (t *RCONTracker) Reset(ip net.IP) {
	if ip == nil {
		return
	}

	ipStr := ip.String()

	// Delete the failure record or reset count
	val, ok := t.failures.Load(ipStr)
	if !ok {
		return
	}

	record, ok := val.(*failureRecord)
	if !ok {
		return
	}

	record.mu.Lock()
	record.count = 0
	record.mu.Unlock()
}
