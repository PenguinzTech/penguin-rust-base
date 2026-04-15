package detect

import (
	"net"
	"testing"
	"time"
)

type stubGeo struct{ locs map[string][2]float64 }

func (s *stubGeo) LookupLatLon(ip net.IP) (float64, float64, bool) {
	loc, ok := s.locs[ip.String()]
	return loc[0], loc[1], ok
}

func newStub(locs map[string][2]float64) *stubGeo {
	return &stubGeo{locs: locs}
}

func TestGeoVelocity_FirstConnect(t *testing.T) {
	stub := newStub(map[string][2]float64{
		"1.2.3.4": {40.7128, -74.0060}, // NYC
	})
	d := newGeoVelocityWithLooker(ModeBlock, 1000.0, stub)
	ip := net.ParseIP("1.2.3.4")
	if d.RecordConnect(ip, 76561198000000001) {
		t.Fatal("first connect should return false")
	}
}

func TestGeoVelocity_SlowTravel(t *testing.T) {
	// NYC → Boston ~306km, 24h → ~12.75 km/h, well under 1000
	stub := newStub(map[string][2]float64{
		"1.2.3.4": {40.7128, -74.0060}, // NYC
		"5.6.7.8": {42.3601, -71.0589}, // Boston
	})
	d := newGeoVelocityWithLooker(ModeBlock, 1000.0, stub)

	steamID := uint64(76561198000000002)
	ip1 := net.ParseIP("1.2.3.4")
	ip2 := net.ParseIP("5.6.7.8")

	// First connect — record NYC
	d.RecordConnect(ip1, steamID)

	// Manually set lastSeen to 24h ago
	rec, _ := d.records.Load(steamID)
	r := rec.(*geoRecord)
	r.mu.Lock()
	r.lastSeen = time.Now().Add(-24 * time.Hour)
	r.mu.Unlock()

	if d.RecordConnect(ip2, steamID) {
		t.Fatal("slow travel should return false")
	}
}

func TestGeoVelocity_ImpossibleTravel(t *testing.T) {
	// NYC → London ~5570km, 1 second → ~20M km/h, way above 1000
	stub := newStub(map[string][2]float64{
		"1.2.3.4": {40.7128, -74.0060}, // NYC
		"9.9.9.9": {51.5074, -0.1278},  // London
	})
	d := newGeoVelocityWithLooker(ModeBlock, 1000.0, stub)

	steamID := uint64(76561198000000003)
	ip1 := net.ParseIP("1.2.3.4")
	ip2 := net.ParseIP("9.9.9.9")

	d.RecordConnect(ip1, steamID)

	// Set lastSeen to 1 second ago
	rec, _ := d.records.Load(steamID)
	r := rec.(*geoRecord)
	r.mu.Lock()
	r.lastSeen = time.Now().Add(-time.Second)
	r.mu.Unlock()

	if !d.RecordConnect(ip2, steamID) {
		t.Fatal("impossible travel should return true")
	}
}

func TestGeoVelocity_UnknownGeo(t *testing.T) {
	stub := newStub(map[string][2]float64{}) // empty — no IPs known
	d := newGeoVelocityWithLooker(ModeBlock, 1000.0, stub)
	ip := net.ParseIP("1.2.3.4")
	if d.RecordConnect(ip, 76561198000000004) {
		t.Fatal("unknown geo should return false")
	}
}

func TestGeoVelocity_ModeOff(t *testing.T) {
	stub := newStub(map[string][2]float64{
		"1.2.3.4": {40.7128, -74.0060},
		"9.9.9.9": {51.5074, -0.1278},
	})
	d := newGeoVelocityWithLooker(ModeOff, 1000.0, stub)

	steamID := uint64(76561198000000005)
	ip1 := net.ParseIP("1.2.3.4")
	ip2 := net.ParseIP("9.9.9.9")

	d.RecordConnect(ip1, steamID)
	if d.RecordConnect(ip2, steamID) {
		t.Fatal("ModeOff should always return false")
	}
}

func TestGeoVelocity_ZeroSteamID(t *testing.T) {
	stub := newStub(map[string][2]float64{
		"1.2.3.4": {40.7128, -74.0060},
	})
	d := newGeoVelocityWithLooker(ModeBlock, 1000.0, stub)
	ip := net.ParseIP("1.2.3.4")
	if d.RecordConnect(ip, 0) {
		t.Fatal("zero steamID should return false")
	}
}
