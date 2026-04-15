package detect

import (
	"math"
	"net"
	"sync"
	"time"

	"github.com/oschwald/geoip2-golang"
)

// geoLooker is an interface so tests can stub geo lookups without a real DB.
type geoLooker interface {
	LookupLatLon(ip net.IP) (lat, lon float64, ok bool)
}

// geoip2Looker wraps a real geoip2.Reader.
type geoip2Looker struct{ db *geoip2.Reader }

func (g *geoip2Looker) LookupLatLon(ip net.IP) (float64, float64, bool) {
	rec, err := g.db.City(ip)
	if err != nil {
		return 0, 0, false
	}
	return rec.Location.Latitude, rec.Location.Longitude, true
}

// GeoVelocityDetector detects impossible geographic travel between connections.
type GeoVelocityDetector struct {
	mode    DetectorMode
	maxKmH  float64  // max plausible travel speed km/h
	geo     geoLooker
	records sync.Map // keyed by uint64 steamID → *geoRecord
}

type geoRecord struct {
	mu       sync.Mutex
	lastLat  float64
	lastLon  float64
	lastSeen time.Time
}

// NewGeoVelocityDetector creates a detector using a real GeoIP2 database file.
// dbPath="" or mode==ModeOff → detector is disabled (always returns false).
func NewGeoVelocityDetector(mode DetectorMode, maxKmH float64, dbPath string) (*GeoVelocityDetector, error) {
	if mode == ModeOff || dbPath == "" {
		return &GeoVelocityDetector{mode: mode, maxKmH: maxKmH}, nil
	}
	db, err := geoip2.Open(dbPath)
	if err != nil {
		return nil, err
	}
	return &GeoVelocityDetector{
		mode:   mode,
		maxKmH: maxKmH,
		geo:    &geoip2Looker{db: db},
	}, nil
}

// newGeoVelocityWithLooker is package-private, for testing only.
func newGeoVelocityWithLooker(mode DetectorMode, maxKmH float64, geo geoLooker) *GeoVelocityDetector {
	return &GeoVelocityDetector{mode: mode, maxKmH: maxKmH, geo: geo}
}

// Mode returns the detector's operating mode.
func (g *GeoVelocityDetector) Mode() DetectorMode { return g.mode }

// RecordConnect checks whether steamID could have physically traveled from its
// last known location to ip's location at the observed time.
// Returns true (impossible travel) if the implied speed > maxKmH.
// steamID==0, nil ip, unknown geo location, first-seen steamID → returns false.
func (g *GeoVelocityDetector) RecordConnect(ip net.IP, steamID uint64) bool {
	if g.mode == ModeOff || g.maxKmH <= 0 || steamID == 0 || ip == nil {
		return false
	}

	lat, lon, ok := g.geo.LookupLatLon(ip)
	if !ok {
		return false
	}

	val, _ := g.records.LoadOrStore(steamID, &geoRecord{})
	rec := val.(*geoRecord)

	rec.mu.Lock()
	defer rec.mu.Unlock()

	if rec.lastSeen.IsZero() {
		rec.lastLat = lat
		rec.lastLon = lon
		rec.lastSeen = time.Now()
		return false
	}

	hours := time.Since(rec.lastSeen).Hours()
	if hours <= 0 {
		return false
	}

	km := haversineKm(rec.lastLat, rec.lastLon, lat, lon)
	speed := km / hours

	rec.lastLat = lat
	rec.lastLon = lon
	rec.lastSeen = time.Now()

	return speed > g.maxKmH
}

func haversineKm(lat1, lon1, lat2, lon2 float64) float64 {
	const R = 6371.0
	dLat := (lat2 - lat1) * math.Pi / 180
	dLon := (lon2 - lon1) * math.Pi / 180
	a := math.Sin(dLat/2)*math.Sin(dLat/2) +
		math.Cos(lat1*math.Pi/180)*math.Cos(lat2*math.Pi/180)*
			math.Sin(dLon/2)*math.Sin(dLon/2)
	return R * 2 * math.Atan2(math.Sqrt(a), math.Sqrt(1-a))
}
