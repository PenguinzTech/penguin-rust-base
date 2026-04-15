package main

import (
	"context"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/api"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/cfg"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/metrics"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/proxy"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/rcon"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/rules"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

func main() {
	// Handle -healthcheck flag
	if len(os.Args) > 1 && os.Args[1] == "-healthcheck" {
		if err := healthcheck(); err != nil {
			os.Exit(1)
		}
		os.Exit(0)
	}

	// Parse environment variables
	gameListenPort := getEnv("WAF_LISTEN_GAME_PORT", "28015")
	gameUpstreamPort := getEnv("WAF_UPSTREAM_GAME_PORT", "28115")
	rconListenPort := getEnv("WAF_LISTEN_RCON_PORT", "28016")
	rconUpstreamPort := getEnv("WAF_UPSTREAM_RCON_PORT", "28116")
	queryListenPort := getEnv("WAF_LISTEN_QUERY_PORT", "28017")
	queryUpstreamPort := getEnv("WAF_UPSTREAM_QUERY_PORT", "28117")
	apiPort := getEnv("WAF_API_PORT", "8080")
	metricsPort := getEnv("WAF_METRICS_PORT", "9090")
	rateLimitPPS := getEnvFloat("WAF_RATE_LIMIT_PPS", 150.0)
	floodThresholdCPS := getEnvFloat("WAF_FLOOD_THRESHOLD_CPS", 10.0)
	rconBanAfter := getEnvInt("WAF_RCON_BAN_AFTER", 5)
	aimbotCV := getEnvFloat("WAF_AIMBOT_CV", 0.05)
	prioritySteamIDsStr := getEnv("WAF_PRIORITY_STEAM_IDS", "")
	usersCfgPath := getEnv("WAF_USERS_CFG_PATH", "")
	bansCfgPath := getEnv("WAF_BANS_CFG_PATH", "")
	cfgPollIntervalStr := getEnv("WAF_CFG_POLL_INTERVAL", "5m")

	cfgPollInterval, err := time.ParseDuration(cfgPollIntervalStr)
	if err != nil {
		log.Fatalf("Invalid WAF_CFG_POLL_INTERVAL: %v", err)
	}

	// Reconnect detector
	reconnectMode := detect.ParseDetectorMode(getEnv("WAF_RECONNECT_MODE", "monitor"))
	reconnectMaxPerWindow := getEnvInt("WAF_RECONNECT_MAX_PER_WINDOW", 10)
	reconnectWindow := getEnvDuration("WAF_RECONNECT_WINDOW", 60*time.Second)

	// Handshake tracker
	handshakeMode := detect.ParseDetectorMode(getEnv("WAF_HANDSHAKE_MODE", "monitor"))
	handshakeMaxPending := getEnvInt("WAF_HANDSHAKE_MAX_PENDING", 20)
	handshakeTimeout := getEnvDuration("WAF_HANDSHAKE_TIMEOUT", 30*time.Second)

	// Entropy detector
	entropyMode := detect.ParseDetectorMode(getEnv("WAF_ENTROPY_MODE", "monitor"))
	entropyThreshold := getEnvFloat("WAF_ENTROPY_THRESHOLD", 7.5)

	// IP churn detector
	ipChurnMode := detect.ParseDetectorMode(getEnv("WAF_IPCHURN_MODE", "monitor"))
	ipChurnMaxIPs := getEnvInt("WAF_IPCHURN_MAX_IPS", 5)
	ipChurnWindow := getEnvDuration("WAF_IPCHURN_WINDOW", 10*time.Minute)

	// Burst detector
	burstMode := detect.ParseDetectorMode(getEnv("WAF_BURST_MODE", "monitor"))
	burstMax := getEnvInt("WAF_BURST_MAX", 200)
	burstWindow := getEnvDuration("WAF_BURST_WINDOW", 1*time.Second)

	// Amplification guard
	amplifyMode := detect.ParseDetectorMode(getEnv("WAF_AMPLIFY_MODE", "monitor"))
	amplifyMaxRatio := getEnvFloat("WAF_AMPLIFY_MAX_RATIO", 50.0)

	// GeoVelocity detector
	geoVelMode := detect.ParseDetectorMode(getEnv("WAF_GEOVEL_MODE", "monitor"))
	geoVelMaxKmH := getEnvFloat("WAF_GEOVEL_MAX_KMH", 1000.0)
	geoVelDBPath := getEnv("WAF_GEOVEL_DB_PATH", "")

	// RCON notifier (for Task 12)
	rconNotifyEnabled := os.Getenv("WAF_RCON_NOTIFY_ENABLED") == "true" || os.Getenv("WAF_RCON_NOTIFY_ENABLED") == "1"
	rconNotifyPassword := getEnv("WAF_RCON_NOTIFY_PASSWORD", "")

	log.Printf("[WAF] Starting WAF sidecar")
	log.Printf("[WAF] Game: %s -> %s", gameListenPort, gameUpstreamPort)
	log.Printf("[WAF] RCON: %s -> %s", rconListenPort, rconUpstreamPort)
	log.Printf("[WAF] Query: %s -> %s", queryListenPort, queryUpstreamPort)
	log.Printf("[WAF] API: :%s, Metrics: :%s", apiPort, metricsPort)
	log.Printf("[WAF] Rate limit: %.0f pps, Flood threshold: %.0f cps", rateLimitPPS, floodThresholdCPS)
	log.Printf("[WAF] RCON ban after: %d failures, Aimbot CV: %.4f", rconBanAfter, aimbotCV)

	// Create state store
	store := state.New()

	// Seed priority SteamIDs
	if prioritySteamIDsStr != "" {
		for _, idStr := range strings.Split(prioritySteamIDsStr, ",") {
			idStr = strings.TrimSpace(idStr)
			if id, err := strconv.ParseUint(idStr, 10, 64); err == nil {
				store.SetPriority(id)
				log.Printf("[WAF] Marked %d as priority", id)
			}
		}
	}

	// Create detectors
	mapper := detect.NewMapper()
	limiter := detect.NewRateLimiter(rateLimitPPS)
	flood := detect.NewFloodDetector(floodThresholdCPS)
	rconTracker := detect.NewRCONTracker(rconBanAfter)
	patterns := detect.NewPatternDetector(aimbotCV)
	reconnect := detect.NewReconnectDetector(reconnectMode, reconnectMaxPerWindow, reconnectWindow)
	handshake := detect.NewHandshakeTracker(handshakeMode, handshakeMaxPending, handshakeTimeout)
	entropy := detect.NewEntropyDetector(entropyMode, entropyThreshold)
	ipChurn := detect.NewIPChurnDetector(ipChurnMode, ipChurnMaxIPs, ipChurnWindow)
	burst := detect.NewBurstDetector(burstMode, burstMax, burstWindow)
	amplify := detect.NewAmplificationGuard(amplifyMode, amplifyMaxRatio)

	var geoVel *detect.GeoVelocityDetector
	if geoVelDBPath != "" {
		geoVel, err = detect.NewGeoVelocityDetector(geoVelMode, geoVelMaxKmH, geoVelDBPath)
		if err != nil {
			log.Printf("[WAF] GeoVelocity detector disabled: %v", err)
			geoVel = nil
		}
	}

	// Create rules engine
	rulesEngine := rules.NewEngine()

	// Create RCON notifier if enabled
	var rconNotifier *rcon.Notifier
	if rconNotifyEnabled {
		rconNotifier = rcon.NewNotifier("ws://127.0.0.1:"+rconUpstreamPort, rconNotifyPassword, 64)
		rconNotifier.Start()
		defer rconNotifier.Stop()
		log.Printf("[WAF] RCON notifier enabled -> ws://127.0.0.1:%s", rconUpstreamPort)
	}

	// Create pipeline
	pipeline := &proxy.Pipeline{
		Store:     store,
		Mapper:    mapper,
		Limiter:   limiter,
		Flood:     flood,
		Patterns:  patterns,
		Rules:     rulesEngine,
		Reconnect: reconnect,
		Handshake: handshake,
		Entropy:   entropy,
		IPChurn:   ipChurn,
		Burst:     burst,
		Amplify:   amplify,
		GeoVel:    geoVel,
		Notifier:  rconNotifier,
	}

	// Create API server
	apiServer := api.New(store, mapper, rulesEngine)

	// Setup context cancellation
	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer cancel()

	var wg sync.WaitGroup

	// Start config poller if configured
	if usersCfgPath != "" || bansCfgPath != "" {
		poller := cfg.New(usersCfgPath, bansCfgPath, cfgPollInterval, store, mapper)
		wg.Add(1)
		go func() {
			defer wg.Done()
			poller.Start(ctx)
		}()
		log.Printf("[WAF] Config poller started with interval %v", cfgPollInterval)
	}

	// Start UDP proxies for game traffic
	gameListenPortInt, _ := strconv.Atoi(gameListenPort)
	udpProxyGame := proxy.NewUDPProxy(":"+gameListenPort, "127.0.0.1:"+gameUpstreamPort, gameListenPortInt, pipeline)
	wg.Add(1)
	go func() {
		defer wg.Done()
		if err := udpProxyGame.Start(ctx); err != nil && err != context.Canceled {
			log.Printf("[WAF] Game UDP proxy error: %v", err)
		}
	}()

	// Start UDP proxy for query traffic
	queryListenPortInt, _ := strconv.Atoi(queryListenPort)
	udpProxyQuery := proxy.NewUDPProxy(":"+queryListenPort, "127.0.0.1:"+queryUpstreamPort, queryListenPortInt, pipeline)
	wg.Add(1)
	go func() {
		defer wg.Done()
		if err := udpProxyQuery.Start(ctx); err != nil && err != context.Canceled {
			log.Printf("[WAF] Query UDP proxy error: %v", err)
		}
	}()

	// Start TCP proxy for RCON
	rconListenPortInt, _ := strconv.Atoi(rconListenPort)
	rconProxy := proxy.NewRCONProxy(":"+rconListenPort, "127.0.0.1:"+rconUpstreamPort, rconListenPortInt, rconTracker, store)
	wg.Add(1)
	go func() {
		defer wg.Done()
		if err := rconProxy.Start(ctx); err != nil && err != context.Canceled {
			log.Printf("[WAF] RCON proxy error: %v", err)
		}
	}()

	// Start API server
	wg.Add(1)
	go func() {
		defer wg.Done()
		if err := apiServer.Start(ctx, ":"+apiPort); err != nil && err != context.Canceled {
			log.Printf("[WAF] API server error: %v", err)
		}
	}()

	// Start metrics HTTP server
	wg.Add(1)
	go func() {
		defer wg.Done()
		srv := &http.Server{
			Addr:    ":" + metricsPort,
			Handler: metrics.Handler(),
		}
		errChan := make(chan error, 1)
		go func() {
			errChan <- srv.ListenAndServe()
		}()

		select {
		case <-ctx.Done():
			shutdownCtx, shutdownCancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer shutdownCancel()
			srv.Shutdown(shutdownCtx)
		case err := <-errChan:
			if err != nil && err != http.ErrServerClosed {
				log.Printf("[WAF] Metrics server error: %v", err)
			}
		}
	}()

	// Wait for context cancellation
	<-ctx.Done()
	log.Println("[WAF] Shutting down")

	// Wait for all goroutines to finish
	wg.Wait()
	log.Println("[WAF] Shutdown complete")
}

func healthcheck() error {
	apiPort := getEnv("WAF_API_PORT", "8080")
	url := fmt.Sprintf("http://localhost:%s/healthz", apiPort)

	client := &http.Client{
		Timeout: 3 * time.Second,
	}

	resp, err := client.Get(url)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("healthz returned status %d", resp.StatusCode)
	}

	return nil
}

func getEnv(key, defaultVal string) string {
	if val := os.Getenv(key); val != "" {
		return val
	}
	return defaultVal
}

func getEnvInt(key string, defaultVal int) int {
	valStr := os.Getenv(key)
	if valStr == "" {
		return defaultVal
	}
	val, err := strconv.Atoi(valStr)
	if err != nil {
		log.Fatalf("Invalid value for %s: %v", key, err)
	}
	return val
}

func getEnvDuration(key string, defaultVal time.Duration) time.Duration {
	valStr := os.Getenv(key)
	if valStr == "" {
		return defaultVal
	}
	val, err := time.ParseDuration(valStr)
	if err != nil {
		log.Printf("[WAF] Invalid value for %s: %v, using default %v", key, err, defaultVal)
		return defaultVal
	}
	return val
}

func getEnvFloat(key string, defaultVal float64) float64 {
	valStr := os.Getenv(key)
	if valStr == "" {
		return defaultVal
	}
	val, err := strconv.ParseFloat(valStr, 64)
	if err != nil {
		log.Fatalf("Invalid value for %s: %v", key, err)
	}
	return val
}
