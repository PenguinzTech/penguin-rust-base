package detect

import (
	"net"
	"testing"
	"time"
)

func TestIPChurnDetector_SingleIP(t *testing.T) {
	// Same IP many times should never trigger churn (still 1 unique IP)
	d := NewIPChurnDetector(3, 1*time.Second)
	steamID := uint64(76561198012345678)

	ip := net.ParseIP("192.168.1.1")
	for i := 0; i < 5; i++ {
		if d.RecordConnect(ip, steamID) {
			t.Errorf("iteration %d: expected false, got true (same IP)", i)
		}
	}
}

func TestIPChurnDetector_ManyIPs(t *testing.T) {
	// maxIPs=3, connect from 4 distinct IPs → true on 4th
	d := NewIPChurnDetector(3, 1*time.Second)
	steamID := uint64(76561198012345678)

	ips := []string{
		"192.168.1.1",
		"192.168.1.2",
		"192.168.1.3",
		"192.168.1.4",
	}

	for i, ipStr := range ips {
		ip := net.ParseIP(ipStr)
		result := d.RecordConnect(ip, steamID)
		if i < 3 && result {
			t.Errorf("IP %d: expected false, got true", i+1)
		}
		if i == 3 && !result {
			t.Errorf("IP %d (4th): expected true, got false", i+1)
		}
	}
}

func TestIPChurnDetector_WindowExpiry(t *testing.T) {
	// Fill to threshold, wait for window to expire, fresh IP → false (old IPs pruned)
	window := 50 * time.Millisecond
	d := NewIPChurnDetector(2, window)
	steamID := uint64(76561198012345678)

	// Add 2 IPs (at threshold, not yet exceeded)
	ip1 := net.ParseIP("192.168.1.1")
	ip2 := net.ParseIP("192.168.1.2")

	if d.RecordConnect(ip1, steamID) {
		t.Errorf("IP 1: expected false, got true")
	}
	if d.RecordConnect(ip2, steamID) {
		t.Errorf("IP 2: expected false, got true")
	}

	// Wait for window to expire
	time.Sleep(60 * time.Millisecond)

	// Add a third IP → old IPs should be pruned, so only 1 unique IP now
	ip3 := net.ParseIP("192.168.1.3")
	if d.RecordConnect(ip3, steamID) {
		t.Errorf("IP 3 (after expiry): expected false, got true (old IPs should be pruned)")
	}
}

func TestIPChurnDetector_ZeroSteamID(t *testing.T) {
	// steamID=0 → always false
	d := NewIPChurnDetector(1, 1*time.Second)
	ip := net.ParseIP("192.168.1.1")

	for i := 0; i < 5; i++ {
		if d.RecordConnect(ip, 0) {
			t.Errorf("iteration %d: expected false for steamID=0, got true", i)
		}
	}
}

func TestIPChurnDetector_Disabled(t *testing.T) {
	// maxIPs <= 0 → always false
	tests := []struct {
		name   string
		maxIPs int
	}{
		{"maxIPs=0", 0},
		{"maxIPs=-1", -1},
		{"maxIPs=-100", -100},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			d := NewIPChurnDetector(tt.maxIPs, 1*time.Second)
			steamID := uint64(76561198012345678)

			// Even with many IPs, should always be false
			for i := 0; i < 10; i++ {
				ipStr := net.IPv4(192, 168, 1, byte(i+1))
				if d.RecordConnect(ipStr, steamID) {
					t.Errorf("iteration %d: expected false for disabled detector, got true", i)
				}
			}
		})
	}
}

func TestIPChurnDetector_IndependentSteamIDs(t *testing.T) {
	// Two steamIDs each churn on their own counter independently
	d := NewIPChurnDetector(2, 1*time.Second)
	steamID1 := uint64(76561198012345678)
	steamID2 := uint64(76561198087654321)

	// steamID1: add 2 IPs
	ip1a := net.ParseIP("192.168.1.1")
	ip1b := net.ParseIP("192.168.1.2")
	if d.RecordConnect(ip1a, steamID1) {
		t.Errorf("steamID1, IP 1: expected false, got true")
	}
	if d.RecordConnect(ip1b, steamID1) {
		t.Errorf("steamID1, IP 2: expected false, got true")
	}

	// steamID2: add 1 IP (should not affect steamID1's count)
	ip2a := net.ParseIP("192.168.2.1")
	if d.RecordConnect(ip2a, steamID2) {
		t.Errorf("steamID2, IP 1: expected false, got true")
	}

	// steamID2: add 2nd IP (still at threshold)
	ip2b := net.ParseIP("192.168.2.2")
	if d.RecordConnect(ip2b, steamID2) {
		t.Errorf("steamID2, IP 2: expected false, got true")
	}

	// steamID1: add 3rd IP → should churn
	ip1c := net.ParseIP("192.168.1.3")
	if !d.RecordConnect(ip1c, steamID1) {
		t.Errorf("steamID1, IP 3: expected true (threshold exceeded), got false")
	}

	// steamID2: add 3rd IP → should churn
	ip2c := net.ParseIP("192.168.2.3")
	if !d.RecordConnect(ip2c, steamID2) {
		t.Errorf("steamID2, IP 3: expected true (threshold exceeded), got false")
	}
}
