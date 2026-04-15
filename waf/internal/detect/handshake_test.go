package detect

import (
	"net"
	"testing"
	"time"
)

func TestHandshakeTracker_NormalFlow(t *testing.T) {
	h := NewHandshakeTracker(5, 10*time.Second)
	ip := net.ParseIP("2.2.2.2")
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
	for i := 0; i < 10; i++ {
		h.RecordPacket(ip1)
	}
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
	h.RecordCompletion(nil) // should not panic
}

func TestHandshakeTracker_CompletionResetsCount(t *testing.T) {
	h := NewHandshakeTracker(3, 10*time.Second)
	ip := net.ParseIP("7.7.7.7")
	h.RecordPacket(ip)
	h.RecordPacket(ip)
	h.RecordCompletion(ip)
	for i := 0; i < 3; i++ {
		if h.RecordPacket(ip) {
			t.Fatalf("should not trigger after completion reset (attempt %d)", i+1)
		}
	}
}
