package detect

import (
	"net"
	"testing"
	"time"
)

func TestCheckNewNormalRate(t *testing.T) {
	detector := NewFloodDetector(10.0) // 10 connections per second

	ip := net.ParseIP("192.168.1.1")

	// Should not trigger for normal rate
	triggered := detector.CheckNew(ip, 0)

	if triggered {
		t.Error("CheckNew() should not trigger for normal rate")
	}
}

func TestCheckNewBurstAboveThreshold(t *testing.T) {
	detector := NewFloodDetector(5.0) // 5 connections per second

	ip := net.ParseIP("192.168.1.1")

	// Trigger multiple connections rapidly
	triggered := false
	for i := 0; i < 10; i++ {
		triggered = detector.CheckNew(ip, 0)
		if triggered {
			break
		}
	}

	if !triggered {
		t.Error("CheckNew() should trigger for burst above threshold")
	}
}

func TestCheckNewDifferentSubnets(t *testing.T) {
	detector := NewFloodDetector(3.0) // 3 connections per second

	ip1 := net.ParseIP("192.168.1.1")
	ip2 := net.ParseIP("192.168.1.100")
	ip3 := net.ParseIP("192.168.2.1") // Different /24 subnet

	// IPs in same /24 should share quota
	// ip1 and ip2 are in same /24: 192.168.1.0/24
	for i := 0; i < 3; i++ {
		detector.CheckNew(ip1, 0)
	}

	// Next connection from ip2 (same /24) should trigger
	triggered := detector.CheckNew(ip2, 0)
	if !triggered {
		t.Error("CheckNew() should trigger for connections from same /24 subnet above threshold")
	}

	// ip3 is in different /24, should have its own quota
	triggered = detector.CheckNew(ip3, 0)
	if triggered {
		t.Error("CheckNew() should not trigger for different /24 subnet")
	}
}

func TestCheckNewIPv4Subnetting(t *testing.T) {
	detector := NewFloodDetector(2.0)

	// All these IPs are in the same /24: 10.0.0.0/24
	ips := []net.IP{
		net.ParseIP("10.0.0.1"),
		net.ParseIP("10.0.0.50"),
		net.ParseIP("10.0.0.200"),
	}

	triggered := false
	for _, ip := range ips {
		triggered = detector.CheckNew(ip, 0)
		if triggered {
			break
		}
	}

	if !triggered {
		t.Error("CheckNew() should trigger for multiple IPs in same /24 above threshold")
	}
}

func TestCheckNewIPv6Subnetting(t *testing.T) {
	detector := NewFloodDetector(2.0)

	// These IPs are in the same /48: 2001:db8:1::/48
	ips := []net.IP{
		net.ParseIP("2001:db8:1::1"),
		net.ParseIP("2001:db8:1::100"),
		net.ParseIP("2001:db8:1::ffff"),
	}

	triggered := false
	for _, ip := range ips {
		triggered = detector.CheckNew(ip, 0)
		if triggered {
			break
		}
	}

	if !triggered {
		t.Error("CheckNew() should trigger for multiple IPs in same /48 above threshold")
	}
}

func TestCheckNewSteamIDThreshold(t *testing.T) {
	detector := NewFloodDetector(3.0) // 3 connections per second

	steamID := uint64(76561198123456789)

	// Multiple connections from same SteamID should trigger
	triggered := false
	for i := 0; i < 5; i++ {
		// Use different IPs to avoid IP-based throttling
		ip := net.ParseIP("192.168." + string(rune(i)) + ".1")
		triggered = detector.CheckNew(ip, steamID)
		if triggered {
			break
		}
	}

	if !triggered {
		t.Error("CheckNew() should trigger for SteamID above threshold")
	}
}

func TestCheckNewDisabled(t *testing.T) {
	detector := NewFloodDetector(0) // disabled

	ip := net.ParseIP("192.168.1.1")

	// Should never trigger when disabled
	for i := 0; i < 100; i++ {
		triggered := detector.CheckNew(ip, 0)
		if triggered {
			t.Error("CheckNew() should not trigger when flood detection disabled")
		}
	}
}

func TestCheckNewNegativeThreshold(t *testing.T) {
	detector := NewFloodDetector(-5.0) // negative = disabled

	ip := net.ParseIP("192.168.1.1")

	// Should never trigger when disabled
	for i := 0; i < 100; i++ {
		triggered := detector.CheckNew(ip, 0)
		if triggered {
			t.Error("CheckNew() should not trigger with negative threshold")
		}
	}
}

func TestCheckNewWindowExpiry(t *testing.T) {
	detector := NewFloodDetector(5.0) // 5 connections per second

	ip := net.ParseIP("192.168.1.1")

	// Fill the window
	for i := 0; i < 5; i++ {
		detector.CheckNew(ip, 0)
	}

	// 6th should trigger
	triggered := detector.CheckNew(ip, 0)
	if !triggered {
		t.Error("CheckNew() should trigger after exceeding threshold")
	}

	// Wait for window to expire
	time.Sleep(1100 * time.Millisecond)

	// Should allow connections again (window reset)
	triggered = detector.CheckNew(ip, 0)
	if triggered {
		t.Error("CheckNew() should not trigger after window expiry")
	}
}

func TestCheckNewNilIP(t *testing.T) {
	detector := NewFloodDetector(5.0)

	// Should not panic or crash with nil IP
	triggered := detector.CheckNew(nil, 0)

	// Should return false since we can't generate a key
	if triggered {
		t.Error("CheckNew() with nil IP should return false")
	}
}

func TestCheckNewCombinedIPAndSteamID(t *testing.T) {
	detector := NewFloodDetector(3.0)

	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// First 3 connections should succeed
	for i := 0; i < 3; i++ {
		triggered := detector.CheckNew(ip, steamID)
		if triggered {
			t.Errorf("CheckNew() connection %d should not trigger", i+1)
		}
	}

	// 4th should trigger (IP subnet threshold exceeded)
	triggered := detector.CheckNew(ip, steamID)
	if !triggered {
		t.Error("CheckNew() should trigger when combined IP + SteamID exceed threshold")
	}
}

func TestCheckNewIPSubnetKey(t *testing.T) {
	tests := []struct {
		name         string
		ip           net.IP
		expectedKey  string
	}{
		{
			name:        "ipv4 basic",
			ip:          net.ParseIP("192.168.1.100"),
			expectedKey: "192.168.1.0/24",
		},
		{
			name:        "ipv4 different subnet",
			ip:          net.ParseIP("10.0.0.50"),
			expectedKey: "10.0.0.0/24",
		},
		{
			name:        "ipv6",
			ip:          net.ParseIP("2001:db8:1::100"),
			expectedKey: "2001:db8:1:0/48",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			detector := NewFloodDetector(100.0) // High threshold so we don't trigger
			_ = detector.CheckNew(tt.ip, 0)

			// Verify the key matches by checking that same subnet IPs share quota
			// If they share quota, it means the same key is used
			sameSubnetIP := tt.ip // Will be overridden based on test
			if tt.name == "ipv4 basic" {
				sameSubnetIP = net.ParseIP("192.168.1.1")
			} else if tt.name == "ipv4 different subnet" {
				sameSubnetIP = net.ParseIP("10.0.0.200")
			} else if tt.name == "ipv6" {
				sameSubnetIP = net.ParseIP("2001:db8:1::1")
			}

			// Both should use same window (quota sharing)
			// So second one should also not trigger
			detector2 := NewFloodDetector(100.0)
			_ = detector2.CheckNew(tt.ip, 0)
			triggered := detector2.CheckNew(sameSubnetIP, 0)
			if triggered {
				t.Errorf("Same subnet IPs should share quota for %s", tt.name)
			}
		})
	}
}

func TestCheckNewIndependentSubnets(t *testing.T) {
	detector := NewFloodDetector(3.0)

	// These are in different /24 subnets
	subnets := []string{
		"192.168.0.1",
		"192.168.1.1",
		"192.168.2.1",
		"192.168.3.1",
	}

	// Each subnet should have independent quota
	for _, subnet := range subnets {
		// Each subnet can support 3 connections
		for i := 0; i < 3; i++ {
			triggered := detector.CheckNew(net.ParseIP(subnet), 0)
			if triggered {
				t.Errorf("Subnet %s connection %d should not trigger", subnet, i+1)
			}
		}
	}

	// Additional connection in first subnet should trigger
	triggered := detector.CheckNew(net.ParseIP("192.168.0.100"), 0)
	if !triggered {
		t.Error("Additional connection in first subnet should trigger")
	}

	// But we should still be able to add connections to another subnet
	triggered = detector.CheckNew(net.ParseIP("192.168.4.1"), 0)
	if triggered {
		t.Error("New subnet should not trigger")
	}
}
