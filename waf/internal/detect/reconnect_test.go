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
	if d.RecordAuth(ip, steamID) {
		t.Fatal("should not trigger after window expiry")
	}
}

func TestReconnectDetector_ZeroSteamID(t *testing.T) {
	d := NewReconnectDetector(1, time.Minute)
	ip := net.ParseIP("1.2.3.4")
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
	d.RecordAuth(ip, steamA)
	d.RecordAuth(ip, steamA)
	d.RecordAuth(ip, steamA)
	if d.RecordAuth(ip, steamB) {
		t.Fatal("steamB should not be affected by steamA's count")
	}
}
