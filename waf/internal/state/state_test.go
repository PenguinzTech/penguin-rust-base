package state

import (
	"net"
	"sync"
	"testing"
	"time"
)

func TestBlockIPAndIsIPBlocked(t *testing.T) {
	tests := []struct {
		name        string
		ip          net.IP
		duration    time.Duration
		reason      string
		shouldBlock bool
		waitTime    time.Duration
	}{
		{
			name:        "block ip permanent",
			ip:          net.ParseIP("192.168.1.1"),
			duration:    0,
			reason:      "test",
			shouldBlock: true,
			waitTime:    0,
		},
		{
			name:        "block ip with expiry not expired",
			ip:          net.ParseIP("10.0.0.1"),
			duration:    10 * time.Millisecond,
			reason:      "test",
			shouldBlock: true,
			waitTime:    0,
		},
		{
			name:        "block ip with expiry expired",
			ip:          net.ParseIP("172.16.0.1"),
			duration:    1 * time.Millisecond,
			reason:      "test",
			shouldBlock: false,
			waitTime:    5 * time.Millisecond,
		},
		{
			name:        "nil ip",
			ip:          nil,
			duration:    0,
			reason:      "test",
			shouldBlock: false,
			waitTime:    0,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s := New()
			s.BlockIP(tt.ip, tt.duration, tt.reason)

			if tt.waitTime > 0 {
				time.Sleep(tt.waitTime)
			}

			got := s.IsIPBlocked(tt.ip)
			if got != tt.shouldBlock {
				t.Errorf("IsIPBlocked() = %v, want %v", got, tt.shouldBlock)
			}
		})
	}
}

func TestBlockSteamIDAndIsSteamIDBlocked(t *testing.T) {
	tests := []struct {
		name        string
		steamID     uint64
		duration    time.Duration
		reason      string
		shouldBlock bool
		waitTime    time.Duration
	}{
		{
			name:        "block steamid permanent",
			steamID:     76561198123456789,
			duration:    0,
			reason:      "test",
			shouldBlock: true,
			waitTime:    0,
		},
		{
			name:        "block steamid with expiry not expired",
			steamID:     76561198987654321,
			duration:    10 * time.Millisecond,
			reason:      "test",
			shouldBlock: true,
			waitTime:    0,
		},
		{
			name:        "block steamid with expiry expired",
			steamID:     76561198111222333,
			duration:    1 * time.Millisecond,
			reason:      "test",
			shouldBlock: false,
			waitTime:    5 * time.Millisecond,
		},
		{
			name:        "zero steamid",
			steamID:     0,
			duration:    0,
			reason:      "test",
			shouldBlock: false,
			waitTime:    0,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s := New()
			s.BlockSteamID(tt.steamID, tt.duration, tt.reason)

			if tt.waitTime > 0 {
				time.Sleep(tt.waitTime)
			}

			got := s.IsSteamIDBlocked(tt.steamID)
			if got != tt.shouldBlock {
				t.Errorf("IsSteamIDBlocked() = %v, want %v", got, tt.shouldBlock)
			}
		})
	}
}

func TestAllowIPAndDisallowIP(t *testing.T) {
	tests := []struct {
		name      string
		ip        net.IP
		allow     bool
		shouldExist bool
	}{
		{
			name:      "allow and check ip",
			ip:        net.ParseIP("192.168.1.1"),
			allow:     true,
			shouldExist: true,
		},
		{
			name:      "disallow ip",
			ip:        net.ParseIP("10.0.0.1"),
			allow:     true,
			shouldExist: false,
		},
		{
			name:      "check ip not allowed",
			ip:        net.ParseIP("172.16.0.1"),
			allow:     false,
			shouldExist: false,
		},
		{
			name:      "nil ip",
			ip:        nil,
			allow:     true,
			shouldExist: false,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s := New()
			if tt.allow {
				s.AllowIP(tt.ip)
			}

			if tt.name == "disallow ip" {
				s.DisallowIP(tt.ip)
				tt.shouldExist = false
			}

			got := s.IsIPAllowed(tt.ip)
			if got != tt.shouldExist {
				t.Errorf("IsIPAllowed() = %v, want %v", got, tt.shouldExist)
			}
		})
	}
}

func TestSetPriorityAndIsPriority(t *testing.T) {
	tests := []struct {
		name          string
		steamID       uint64
		setPriority   bool
		shouldBeAdmin bool
	}{
		{
			name:          "set priority",
			steamID:       76561198123456789,
			setPriority:   true,
			shouldBeAdmin: true,
		},
		{
			name:          "unset priority",
			steamID:       76561198987654321,
			setPriority:   true,
			shouldBeAdmin: false,
		},
		{
			name:          "check non-priority",
			steamID:       76561198111222333,
			setPriority:   false,
			shouldBeAdmin: false,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s := New()
			if tt.setPriority {
				s.SetPriority(tt.steamID)
			}

			if tt.name == "unset priority" {
				s.UnsetPriority(tt.steamID)
			}

			got := s.IsPriority(tt.steamID)
			if got != tt.shouldBeAdmin {
				t.Errorf("IsPriority() = %v, want %v", got, tt.shouldBeAdmin)
			}
		})
	}
}

func TestBlockIPRejectedWhenTargetIPMapsToPrioritySteamID(t *testing.T) {
	s := New()
	ip := net.ParseIP("192.168.1.1")
	steamID := uint64(76561198123456789)

	// Simulate IP mapping to priority SteamID
	s.SetPriority(steamID)
	mapper := NewMapperForTest()
	mapper.RecordForTest(ip, steamID)

	// Manually associate IP with SteamID in store for this test
	s.ipToSteam = mapper.ipToSteam

	// Try to block the IP
	s.BlockIP(ip, 0, "test")

	// Should not be blocked because SteamID is priority
	if s.IsIPBlocked(ip) {
		t.Error("BlockIP should be rejected when target IP maps to priority SteamID")
	}
}

func TestBlockSteamIDRejectedWhenSteamIDIsPriority(t *testing.T) {
	s := New()
	steamID := uint64(76561198123456789)

	// Mark as priority
	s.SetPriority(steamID)

	// Try to block
	s.BlockSteamID(steamID, 0, "test")

	// Should not be blocked
	if s.IsSteamIDBlocked(steamID) {
		t.Error("BlockSteamID should be rejected when SteamID is priority")
	}
}

func TestThrottleIPAndGetThrottle(t *testing.T) {
	tests := []struct {
		name        string
		ip          net.IP
		factor      float64
		duration    time.Duration
		shouldThrottle bool
		waitTime    time.Duration
	}{
		{
			name:        "throttle ip active",
			ip:          net.ParseIP("192.168.1.1"),
			factor:      0.5,
			duration:    10 * time.Millisecond,
			shouldThrottle: true,
			waitTime:    0,
		},
		{
			name:        "throttle ip expired",
			ip:          net.ParseIP("10.0.0.1"),
			factor:      0.5,
			duration:    1 * time.Millisecond,
			shouldThrottle: false,
			waitTime:    5 * time.Millisecond,
		},
		{
			name:        "nil ip",
			ip:          nil,
			factor:      0.5,
			duration:    10 * time.Millisecond,
			shouldThrottle: false,
			waitTime:    0,
		},
		{
			name:        "negative duration",
			ip:          net.ParseIP("172.16.0.1"),
			factor:      0.5,
			duration:    -1 * time.Second,
			shouldThrottle: false,
			waitTime:    0,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			s := New()
			s.ThrottleIP(tt.ip, tt.factor, tt.duration)

			if tt.waitTime > 0 {
				time.Sleep(tt.waitTime)
			}

			factor, got := s.GetThrottle(tt.ip)
			if got != tt.shouldThrottle {
				t.Errorf("GetThrottle() returned %v, want %v", got, tt.shouldThrottle)
			}

			if got && factor != tt.factor {
				t.Errorf("GetThrottle() factor = %v, want %v", factor, tt.factor)
			}
		})
	}
}

func TestBlockedIPsCount(t *testing.T) {
	s := New()

	// Block multiple IPs
	ips := []net.IP{
		net.ParseIP("192.168.1.1"),
		net.ParseIP("10.0.0.1"),
		net.ParseIP("172.16.0.1"),
	}

	for _, ip := range ips {
		s.BlockIP(ip, 0, "test")
	}

	count := s.BlockedIPsCount()
	if count != len(ips) {
		t.Errorf("BlockedIPsCount() = %d, want %d", count, len(ips))
	}

	// Unblock one
	s.UnblockIP(ips[0])
	count = s.BlockedIPsCount()
	if count != len(ips)-1 {
		t.Errorf("BlockedIPsCount() after unblock = %d, want %d", count, len(ips)-1)
	}
}

func TestBlockedSteamIDsCount(t *testing.T) {
	s := New()

	steamIDs := []uint64{
		76561198123456789,
		76561198987654321,
		76561198111222333,
	}

	for _, id := range steamIDs {
		s.BlockSteamID(id, 0, "test")
	}

	count := s.BlockedSteamIDsCount()
	if count != len(steamIDs) {
		t.Errorf("BlockedSteamIDsCount() = %d, want %d", count, len(steamIDs))
	}

	// Unblock one
	s.UnblockSteamID(steamIDs[0])
	count = s.BlockedSteamIDsCount()
	if count != len(steamIDs)-1 {
		t.Errorf("BlockedSteamIDsCount() after unblock = %d, want %d", count, len(steamIDs)-1)
	}
}

func TestIsPriorityBlocked(t *testing.T) {
	s := New()
	steamID := uint64(76561198123456789)

	s.SetPriority(steamID)

	// Priority IDs can never be blocked
	if s.IsPriorityBlocked(steamID) {
		t.Error("IsPriorityBlocked() should always return false")
	}
}

func TestUnblockIP(t *testing.T) {
	s := New()
	ip := net.ParseIP("192.168.1.1")

	s.BlockIP(ip, 0, "test")
	if !s.IsIPBlocked(ip) {
		t.Error("IP should be blocked")
	}

	s.UnblockIP(ip)
	if s.IsIPBlocked(ip) {
		t.Error("IP should be unblocked")
	}
}

func TestUnblockSteamID(t *testing.T) {
	s := New()
	steamID := uint64(76561198123456789)

	s.BlockSteamID(steamID, 0, "test")
	if !s.IsSteamIDBlocked(steamID) {
		t.Error("SteamID should be blocked")
	}

	s.UnblockSteamID(steamID)
	if s.IsSteamIDBlocked(steamID) {
		t.Error("SteamID should be unblocked")
	}
}

// Helper types for testing
type testMapper struct {
	ipToSteam sync.Map
}

func NewMapperForTest() *testMapper {
	return &testMapper{}
}

func (tm *testMapper) RecordForTest(ip net.IP, steamID uint64) {
	if ip == nil || steamID == 0 {
		return
	}
	tm.ipToSteam.Store(ip.String(), steamID)
}
