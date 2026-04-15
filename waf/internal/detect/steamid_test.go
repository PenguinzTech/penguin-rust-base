package detect

import (
	"encoding/binary"
	"fmt"
	"net"
	"testing"
)

func TestExtract(t *testing.T) {
	tests := []struct {
		name        string
		payload     []byte
		expectFound bool
		expectedID  uint64
	}{
		{
			name: "valid steam64 id little endian",
			payload: func() []byte {
				id := uint64(0x0110000100000001)
				b := make([]byte, 8)
				binary.LittleEndian.PutUint64(b, id)
				return b
			}(),
			expectFound: true,
			expectedID:  0x0110000100000001,
		},
		{
			name: "valid steam64 id big endian",
			payload: func() []byte {
				id := uint64(0x0110000100000002)
				b := make([]byte, 8)
				binary.BigEndian.PutUint64(b, id)
				return b
			}(),
			expectFound: true,
			expectedID:  0x0110000100000002,
		},
		{
			name:        "payload too short",
			payload:     []byte{0x01, 0x10, 0x00, 0x00},
			expectFound: false,
			expectedID:  0,
		},
		{
			name:        "payload with no valid id",
			payload:     []byte{0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF},
			expectFound: false,
			expectedID:  0,
		},
		{
			name: "valid id in middle of payload",
			payload: func() []byte {
				p := make([]byte, 16)
				id := uint64(0x0110000100000003)
				binary.LittleEndian.PutUint64(p[4:12], id)
				return p
			}(),
			expectFound: true,
			expectedID:  0x0110000100000003,
		},
		{
			name:        "empty payload",
			payload:     []byte{},
			expectFound: false,
			expectedID:  0,
		},
		{
			name:        "nil payload",
			payload:     nil,
			expectFound: false,
			expectedID:  0,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			m := NewMapper()
			id, found := m.Extract(tt.payload)

			if found != tt.expectFound {
				t.Errorf("Extract() found = %v, want %v", found, tt.expectFound)
			}

			if found && id != tt.expectedID {
				t.Errorf("Extract() id = %v, want %v", id, tt.expectedID)
			}
		})
	}
}

func TestRecordAndSteamForIP(t *testing.T) {
	tests := []struct {
		name        string
		ip          net.IP
		steamID     uint64
		expectFound bool
	}{
		{
			name:        "record and retrieve ip",
			ip:          net.ParseIP("192.168.1.1"),
			steamID:     76561198123456789,
			expectFound: true,
		},
		{
			name:        "nil ip",
			ip:          nil,
			steamID:     76561198987654321,
			expectFound: false,
		},
		{
			name:        "zero steamid",
			ip:          net.ParseIP("10.0.0.1"),
			steamID:     0,
			expectFound: false,
		},
		{
			name:        "ipv6 address",
			ip:          net.ParseIP("2001:db8::1"),
			steamID:     76561198111222333,
			expectFound: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			m := NewMapper()
			m.Record(tt.ip, tt.steamID)

			id, found := m.SteamForIP(tt.ip)

			if found != tt.expectFound {
				t.Errorf("SteamForIP() found = %v, want %v", found, tt.expectFound)
			}

			if found && id != tt.steamID {
				t.Errorf("SteamForIP() id = %v, want %v", id, tt.steamID)
			}
		})
	}
}

func TestIPsForSteam(t *testing.T) {
	m := NewMapper()

	steamID := uint64(76561198123456789)
	ips := []net.IP{
		net.ParseIP("192.168.1.1"),
		net.ParseIP("10.0.0.1"),
		net.ParseIP("172.16.0.1"),
	}

	// Record all IPs for same SteamID
	for _, ip := range ips {
		m.Record(ip, steamID)
	}

	result := m.IPsForSteam(steamID)

	if len(result) != len(ips) {
		t.Errorf("IPsForSteam() returned %d ips, want %d", len(result), len(ips))
	}

	// Check all IPs are present
	ipMap := make(map[string]bool)
	for _, ip := range result {
		ipMap[ip.String()] = true
	}

	for _, ip := range ips {
		if !ipMap[ip.String()] {
			t.Errorf("IPsForSteam() missing IP %s", ip.String())
		}
	}
}

func TestIPsForSteamNotFound(t *testing.T) {
	m := NewMapper()

	result := m.IPsForSteam(76561198123456789)

	if len(result) != 0 {
		t.Errorf("IPsForSteam() for unknown steamid should return empty, got %d ips", len(result))
	}
}

func TestCount(t *testing.T) {
	m := NewMapper()

	steamIDs := []uint64{
		76561198123456789,
		76561198987654321,
		76561198111222333,
	}

	for i, steamID := range steamIDs {
		// Each SteamID gets mapped to a unique IP
		ip := net.ParseIP(fmt.Sprintf("192.168.1.%d", i+1))
		m.Record(ip, steamID)

		count := m.Count()
		if count != i+1 {
			t.Errorf("Count() after recording steamid %d = %d, want %d", steamID, count, i+1)
		}
	}
}

func TestRecordDuplicate(t *testing.T) {
	m := NewMapper()

	steamID := uint64(76561198123456789)
	ip := net.ParseIP("192.168.1.1")

	// Record same mapping twice
	m.Record(ip, steamID)
	m.Record(ip, steamID)

	result := m.IPsForSteam(steamID)

	// Should only have one entry despite duplicate record
	if len(result) != 1 {
		t.Errorf("IPsForSteam() after duplicate record has %d ips, want 1", len(result))
	}
}

func TestMultipleSteamIDsSameIP(t *testing.T) {
	m := NewMapper()

	ip := net.ParseIP("192.168.1.1")
	steamID1 := uint64(76561198123456789)
	steamID2 := uint64(76561198987654321)

	// Record same IP with different SteamIDs (later one overwrites)
	m.Record(ip, steamID1)
	m.Record(ip, steamID2)

	// IP should be associated with the last recorded SteamID
	id, found := m.SteamForIP(ip)

	if !found {
		t.Error("SteamForIP() should find the IP")
	}

	if id != steamID2 {
		t.Errorf("SteamForIP() returned %d, want %d", id, steamID2)
	}
}

func TestRecordMultipleIPsForSameSteamID(t *testing.T) {
	m := NewMapper()

	steamID := uint64(76561198123456789)
	ips := []net.IP{
		net.ParseIP("192.168.1.1"),
		net.ParseIP("10.0.0.1"),
		net.ParseIP("172.16.0.1"),
	}

	for _, ip := range ips {
		m.Record(ip, steamID)
	}

	// All IPs should be retrievable
	result := m.IPsForSteam(steamID)

	if len(result) != len(ips) {
		t.Errorf("IPsForSteam() returned %d ips, want %d", len(result), len(ips))
	}

	// Each IP should map back to the same SteamID
	for _, ip := range ips {
		id, found := m.SteamForIP(ip)
		if !found || id != steamID {
			t.Errorf("SteamForIP(%s) = %d, %v; want %d, true", ip.String(), id, found, steamID)
		}
	}
}
