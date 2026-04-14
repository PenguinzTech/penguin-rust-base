package metrics

import (
	"net/http"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/promauto"
	"github.com/prometheus/client_golang/prometheus/promhttp"
)

var (
	PacketsTotal = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_packets_total",
			Help: "Total packets processed by the WAF",
		},
		[]string{"port", "action"},
	)

	BlockedIPsGauge = promauto.NewGauge(
		prometheus.GaugeOpts{
			Name: "waf_blocked_ips",
			Help: "Current count of blocked IP addresses",
		},
	)

	RCONAuthFailures = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_rcon_auth_failures_total",
			Help: "Total RCON authentication failures",
		},
		[]string{"ip"},
	)

	SuspectsTotal = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_suspects_total",
			Help: "Total suspected malicious actors",
		},
		[]string{"reason"},
	)

	DetectionEvents = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_detection_events_total",
			Help: "Total detection events by heuristic",
		},
		[]string{"heuristic"},
	)

	RuleHits = promauto.NewCounterVec(
		prometheus.CounterOpts{
			Name: "waf_rule_hits_total",
			Help: "Total rule hits",
		},
		[]string{"rule_id", "action"},
	)

	ActiveRulesGauge = promauto.NewGauge(
		prometheus.GaugeOpts{
			Name: "waf_active_rules",
			Help: "Current count of active WAF rules",
		},
	)

	SteamIDMappingsGauge = promauto.NewGauge(
		prometheus.GaugeOpts{
			Name: "waf_steam_id_mappings",
			Help: "Current count of IP to SteamID mappings",
		},
	)

	SteamIDBlocksTotal = promauto.NewCounter(
		prometheus.CounterOpts{
			Name: "waf_steam_id_blocks_total",
			Help: "Total SteamID blocks issued",
		},
	)
)

// Handler returns the Prometheus metrics HTTP handler.
func Handler() http.Handler {
	return promhttp.Handler()
}
