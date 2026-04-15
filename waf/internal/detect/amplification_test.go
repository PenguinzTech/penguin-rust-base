package detect

import (
	"net"
	"testing"
)

func TestAmplificationGuard_NormalRatio(t *testing.T) {
	g := NewAmplificationGuard(ModeBlock, 10.0)
	ip := net.ParseIP("1.2.3.4")
	g.RecordRequest(ip, 100)
	if g.RecordResponse(ip, 100) {
		t.Error("expected false for ratio=1.0, maxRatio=10")
	}
}

func TestAmplificationGuard_ExceedsRatio(t *testing.T) {
	g := NewAmplificationGuard(ModeBlock, 10.0)
	ip := net.ParseIP("1.2.3.4")
	g.RecordRequest(ip, 10)
	if !g.RecordResponse(ip, 1000) {
		t.Error("expected true for ratio=100, maxRatio=10")
	}
}

func TestAmplificationGuard_NoRequestYet(t *testing.T) {
	g := NewAmplificationGuard(ModeBlock, 10.0)
	ip := net.ParseIP("1.2.3.4")
	if g.RecordResponse(ip, 500) {
		t.Error("expected false when no request recorded (avoid div/0)")
	}
}

func TestAmplificationGuard_ModeOff(t *testing.T) {
	g := NewAmplificationGuard(ModeOff, 10.0)
	ip := net.ParseIP("1.2.3.4")
	g.RecordRequest(ip, 10)
	if g.RecordResponse(ip, 10000) {
		t.Error("expected false for ModeOff")
	}
}

func TestAmplificationGuard_NilIP(t *testing.T) {
	g := NewAmplificationGuard(ModeBlock, 10.0)
	defer func() {
		if r := recover(); r != nil {
			t.Errorf("panic on nil IP: %v", r)
		}
	}()
	g.RecordRequest(nil, 10)
	g.RecordResponse(nil, 10000)
}

func TestAmplificationGuard_IndependentIPs(t *testing.T) {
	g := NewAmplificationGuard(ModeBlock, 10.0)
	ip1 := net.ParseIP("1.1.1.1")
	ip2 := net.ParseIP("2.2.2.2")

	g.RecordRequest(ip1, 100)
	g.RecordRequest(ip2, 10)

	// ip1: ratio=1, should not trigger
	if g.RecordResponse(ip1, 100) {
		t.Error("ip1: expected false")
	}
	// ip2: ratio=100, should trigger
	if !g.RecordResponse(ip2, 1000) {
		t.Error("ip2: expected true")
	}
}
