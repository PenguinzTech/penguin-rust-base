package proxy

import (
	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/rules"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

// Pipeline holds all dependencies needed for packet inspection.
type Pipeline struct {
	Store     *state.Store
	Mapper    *detect.Mapper
	Limiter   *detect.RateLimiter
	Flood     *detect.FloodDetector
	Patterns  *detect.PatternDetector
	Rules     *rules.Engine
	Reconnect *detect.ReconnectDetector
	Handshake *detect.HandshakeTracker
	Entropy   *detect.EntropyDetector
	IPChurn   *detect.IPChurnDetector
	Burst     *detect.BurstDetector
	Amplify   *detect.AmplificationGuard
	GeoVel    *detect.GeoVelocityDetector
}
