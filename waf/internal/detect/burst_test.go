package detect

import (
	"net"
	"testing"
	"time"
)

func TestBurstDetector_BelowThreshold(t *testing.T) {
	bd := NewBurstDetector(ModeBlock, 5, 100*time.Millisecond)
	ip := net.ParseIP("192.168.1.1")

	// Send 5 packets (at or below threshold)
	for i := 0; i < 5; i++ {
		got := bd.RecordPacket(ip)
		if got {
			t.Errorf("RecordPacket(%d) = true, want false (at threshold)", i+1)
		}
	}
}

func TestBurstDetector_ExceedsThreshold(t *testing.T) {
	bd := NewBurstDetector(ModeBlock, 5, 100*time.Millisecond)
	ip := net.ParseIP("192.168.1.1")

	// Send 6 packets; 6th should trigger
	for i := 0; i < 5; i++ {
		got := bd.RecordPacket(ip)
		if got {
			t.Errorf("RecordPacket(%d) = true, want false (below threshold)", i+1)
		}
	}

	// 6th packet should exceed threshold
	got := bd.RecordPacket(ip)
	if !got {
		t.Errorf("RecordPacket(6) = false, want true (exceeds threshold)")
	}
}

func TestBurstDetector_WindowExpiry(t *testing.T) {
	bd := NewBurstDetector(ModeBlock, 5, 50*time.Millisecond)
	ip := net.ParseIP("192.168.1.1")

	// Fill to threshold (6 packets to trigger)
	for i := 0; i < 6; i++ {
		bd.RecordPacket(ip)
	}

	// Sleep longer than window
	time.Sleep(60 * time.Millisecond)

	// New packet after window expiry should return false (window reset)
	got := bd.RecordPacket(ip)
	if got {
		t.Errorf("RecordPacket after window expiry = true, want false (window reset)")
	}
}

func TestBurstDetector_ModeOff(t *testing.T) {
	bd := NewBurstDetector(ModeOff, 5, 100*time.Millisecond)
	ip := net.ParseIP("192.168.1.1")

	// Send 10 packets; all should return false because mode is off
	for i := 0; i < 10; i++ {
		got := bd.RecordPacket(ip)
		if got {
			t.Errorf("RecordPacket(%d) with ModeOff = true, want false", i+1)
		}
	}
}

func TestBurstDetector_NilIP(t *testing.T) {
	bd := NewBurstDetector(ModeBlock, 5, 100*time.Millisecond)

	// nil IP should return false
	got := bd.RecordPacket(nil)
	if got {
		t.Errorf("RecordPacket(nil) = true, want false")
	}
}

func TestBurstDetector_IndependentIPs(t *testing.T) {
	bd := NewBurstDetector(ModeBlock, 5, 100*time.Millisecond)
	ip1 := net.ParseIP("192.168.1.1")
	ip2 := net.ParseIP("192.168.1.2")

	// Send 6 packets from ip1 (exceeds threshold)
	for i := 0; i < 6; i++ {
		bd.RecordPacket(ip1)
	}

	// Send 4 packets from ip2 (below threshold)
	for i := 0; i < 4; i++ {
		got := bd.RecordPacket(ip2)
		if got {
			t.Errorf("RecordPacket(ip2, %d) = true, want false (independent tracking)", i+1)
		}
	}

	// 5th packet from ip2 should still be false (below threshold)
	got := bd.RecordPacket(ip2)
	if got {
		t.Errorf("RecordPacket(ip2, 5) = true, want false (at threshold)")
	}

	// 6th packet from ip2 should exceed
	got = bd.RecordPacket(ip2)
	if !got {
		t.Errorf("RecordPacket(ip2, 6) = false, want true (exceeds threshold)")
	}
}

func TestBurstDetector_MaxBurstZero(t *testing.T) {
	bd := NewBurstDetector(ModeBlock, 0, 100*time.Millisecond)
	ip := net.ParseIP("192.168.1.1")

	// maxBurst <= 0 should always return false
	got := bd.RecordPacket(ip)
	if got {
		t.Errorf("RecordPacket with maxBurst=0 = true, want false")
	}
}

func TestBurstDetector_Mode(t *testing.T) {
	tests := []struct {
		name     string
		mode     DetectorMode
		expected DetectorMode
	}{
		{"ModeOff", ModeOff, ModeOff},
		{"ModeMonitor", ModeMonitor, ModeMonitor},
		{"ModeBlock", ModeBlock, ModeBlock},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			bd := NewBurstDetector(tt.mode, 5, 100*time.Millisecond)
			got := bd.Mode()
			if got != tt.expected {
				t.Errorf("Mode() = %v, want %v", got, tt.expected)
			}
		})
	}
}
