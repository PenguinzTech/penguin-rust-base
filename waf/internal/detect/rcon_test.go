package detect

import (
	"net"
	"testing"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

func TestRecordFailureAndIsBlocked(t *testing.T) {
	tests := []struct {
		name         string
		banAfter     int
		failures     int
		shouldBlock  bool
	}{
		{
			name:        "not reached threshold",
			banAfter:    5,
			failures:    4,
			shouldBlock: false,
		},
		{
			name:        "reached threshold",
			banAfter:    5,
			failures:    5,
			shouldBlock: true,
		},
		{
			name:        "exceeded threshold",
			banAfter:    3,
			failures:    10,
			shouldBlock: true,
		},
		{
			name:        "threshold 1",
			banAfter:    1,
			failures:    1,
			shouldBlock: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			tracker := NewRCONTracker(tt.banAfter)
			store := state.New()

			ip := net.ParseIP("192.168.1.1")

			// Record multiple failures
			for i := 0; i < tt.failures; i++ {
				tracker.RecordFailure(ip, store)
			}

			// Check if blocked
			isBlocked := tracker.IsBlocked(ip)
			if isBlocked != tt.shouldBlock {
				t.Errorf("IsBlocked() = %v, want %v", isBlocked, tt.shouldBlock)
			}

			// Should also be blocked in the store if threshold reached
			if tt.shouldBlock && !store.IsIPBlocked(ip) {
				t.Error("IP should be blocked in store when threshold reached")
			}
		})
	}
}

func TestRecordFailureBlocksIP(t *testing.T) {
	tracker := NewRCONTracker(3)
	store := state.New()

	ip := net.ParseIP("192.168.1.1")

	// Record failures
	for i := 0; i < 3; i++ {
		tracker.RecordFailure(ip, store)
	}

	// IP should be blocked in store
	if !store.IsIPBlocked(ip) {
		t.Error("RecordFailure() should block IP in store when threshold reached")
	}
}

func TestIsBlockedNilIP(t *testing.T) {
	tracker := NewRCONTracker(3)

	// Should return false for nil IP
	isBlocked := tracker.IsBlocked(nil)
	if isBlocked {
		t.Error("IsBlocked() with nil IP should return false")
	}
}

func TestRecordFailureNilIP(t *testing.T) {
	tracker := NewRCONTracker(3)
	store := state.New()

	// Should not panic with nil IP
	tracker.RecordFailure(nil, store)

	// Should not block anything
	if tracker.IsBlocked(net.ParseIP("192.168.1.1")) {
		t.Error("RecordFailure() with nil IP should not block anything")
	}
}

func TestRecordFailureNilStore(t *testing.T) {
	// banAfter=1 so a single failure reaches the threshold and IsBlocked returns true
	tracker := NewRCONTracker(1)

	ip := net.ParseIP("192.168.1.1")

	// Should not panic with nil store
	tracker.RecordFailure(ip, nil)

	// Failure should still be recorded (count incremented even when store is nil)
	if !tracker.IsBlocked(ip) {
		t.Error("RecordFailure() should record failure even with nil store")
	}
}

func TestResetClears(t *testing.T) {
	tracker := NewRCONTracker(3)
	store := state.New()

	ip := net.ParseIP("192.168.1.1")

	// Record failures up to threshold
	for i := 0; i < 3; i++ {
		tracker.RecordFailure(ip, store)
	}

	if !tracker.IsBlocked(ip) {
		t.Error("IP should be blocked before reset")
	}

	// Reset
	tracker.Reset(ip)

	if tracker.IsBlocked(ip) {
		t.Error("Reset() should clear failure count")
	}
}

func TestResetNilIP(t *testing.T) {
	tracker := NewRCONTracker(3)

	// Should not panic with nil IP
	tracker.Reset(nil)
}

func TestMultipleIPsIndependent(t *testing.T) {
	tracker := NewRCONTracker(3)
	store := state.New()

	ip1 := net.ParseIP("192.168.1.1")
	ip2 := net.ParseIP("10.0.0.1")

	// Record 2 failures for ip1
	tracker.RecordFailure(ip1, store)
	tracker.RecordFailure(ip1, store)

	// Record 3 failures for ip2 (should trigger block)
	for i := 0; i < 3; i++ {
		tracker.RecordFailure(ip2, store)
	}

	// ip1 should not be blocked
	if tracker.IsBlocked(ip1) {
		t.Error("ip1 should not be blocked")
	}

	// ip2 should be blocked
	if !tracker.IsBlocked(ip2) {
		t.Error("ip2 should be blocked")
	}

	// Only ip2 should be blocked in store
	if store.IsIPBlocked(ip1) {
		t.Error("ip1 should not be blocked in store")
	}

	if !store.IsIPBlocked(ip2) {
		t.Error("ip2 should be blocked in store")
	}
}

func TestRecordFailureIncrementsCount(t *testing.T) {
	tracker := NewRCONTracker(5)
	store := state.New()

	ip := net.ParseIP("192.168.1.1")

	// First failure should not block
	tracker.RecordFailure(ip, store)
	if tracker.IsBlocked(ip) {
		t.Error("Should not be blocked after 1 failure")
	}

	// Multiple failures
	for i := 0; i < 3; i++ {
		tracker.RecordFailure(ip, store)
		if tracker.IsBlocked(ip) {
			t.Errorf("Should not be blocked after %d failures", i+2)
		}
	}

	// Threshold reached
	tracker.RecordFailure(ip, store)
	if !tracker.IsBlocked(ip) {
		t.Error("Should be blocked after reaching threshold")
	}
}

func TestIsBlockedFalseBeforeThreshold(t *testing.T) {
	tracker := NewRCONTracker(5)

	ip := net.ParseIP("192.168.1.1")

	// Record some failures but not enough
	for i := 0; i < 4; i++ {
		tracker.RecordFailure(ip, nil)
		if tracker.IsBlocked(ip) {
			t.Errorf("IsBlocked() should return false before threshold, failed at failure %d", i+1)
		}
	}
}

func TestThresholdBoundary(t *testing.T) {
	tracker := NewRCONTracker(3)
	store := state.New()

	ip := net.ParseIP("192.168.1.1")

	// One before threshold
	tracker.RecordFailure(ip, store)
	tracker.RecordFailure(ip, store)
	if tracker.IsBlocked(ip) || store.IsIPBlocked(ip) {
		t.Error("Should not be blocked at threshold-1")
	}

	// At threshold
	tracker.RecordFailure(ip, store)
	if !tracker.IsBlocked(ip) {
		t.Error("IsBlocked() should return true at threshold")
	}

	if !store.IsIPBlocked(ip) {
		t.Error("IP should be blocked in store at threshold")
	}

	// Beyond threshold
	tracker.RecordFailure(ip, store)
	if !tracker.IsBlocked(ip) {
		t.Error("IsBlocked() should remain true beyond threshold")
	}
}

func TestResetNonExistentIP(t *testing.T) {
	tracker := NewRCONTracker(3)

	// Reset an IP that was never recorded
	tracker.Reset(net.ParseIP("192.168.1.1"))

	// Should not panic or cause issues
	if tracker.IsBlocked(net.ParseIP("192.168.1.1")) {
		t.Error("Non-existent IP should not be blocked after reset")
	}
}

func TestRecordFailureZeroBanAfter(t *testing.T) {
	tracker := NewRCONTracker(0)
	store := state.New()

	ip := net.ParseIP("192.168.1.1")

	// With ban_after=0, first failure should trigger block
	tracker.RecordFailure(ip, store)

	if !tracker.IsBlocked(ip) {
		t.Error("First failure should trigger block with ban_after=0")
	}
}

func TestRecordFailureStoreBlocking(t *testing.T) {
	tracker := NewRCONTracker(2)
	store := state.New()

	ip := net.ParseIP("192.168.1.1")

	// Record failures
	tracker.RecordFailure(ip, store)
	tracker.RecordFailure(ip, store)

	// Should be blocked in store
	if !store.IsIPBlocked(ip) {
		t.Error("IP should be blocked in store")
	}

	// Verify the reason
	// (This checks that BlockIP was called correctly)
	// Since we can't directly inspect the reason, we verify the block
	if !store.IsIPBlocked(ip) {
		t.Error("IP should be permanently blocked with reason 'RCON_AUTH_FAILURE'")
	}
}

func TestConcurrentRecordFailures(t *testing.T) {
	tracker := NewRCONTracker(10)
	store := state.New()

	ip := net.ParseIP("192.168.1.1")

	// Simulate concurrent failures
	done := make(chan bool)
	for i := 0; i < 10; i++ {
		go func() {
			tracker.RecordFailure(ip, store)
			done <- true
		}()
	}

	// Wait for all goroutines
	for i := 0; i < 10; i++ {
		<-done
	}

	// Should be blocked
	if !tracker.IsBlocked(ip) {
		t.Error("IP should be blocked after concurrent failures")
	}

	if !store.IsIPBlocked(ip) {
		t.Error("IP should be blocked in store after concurrent failures")
	}
}
