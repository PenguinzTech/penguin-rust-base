package proxy

import (
	"context"
	"fmt"
	"log"
	"net"
	"strconv"
	"sync"
	"time"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/metrics"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/rules"
)

// UDPProxy proxies UDP traffic with WAF inspection.
type UDPProxy struct {
	listenAddr   string
	upstreamAddr string
	pipeline     *Pipeline
	port         int
	listener     *net.UDPConn  // public-facing listener, used to write responses back to clients
	connMap      sync.Map      // key: string(srcAddr) → *net.UDPConn
	clientMap    sync.Map      // key: string(upstreamConn.LocalAddr) → *net.UDPAddr (client addr)
}

// NewUDPProxy creates a new UDP proxy.
func NewUDPProxy(listenAddr, upstreamAddr string, port int, p *Pipeline) *UDPProxy {
	return &UDPProxy{
		listenAddr:   listenAddr,
		upstreamAddr: upstreamAddr,
		pipeline:     p,
		port:         port,
	}
}

// Start listens on listenAddr, applies the 12-step inspection pipeline to each
// inbound packet, and forwards clean packets to upstreamAddr.
func (u *UDPProxy) Start(ctx context.Context) error {
	udpAddr, err := net.ResolveUDPAddr("udp", u.listenAddr)
	if err != nil {
		return fmt.Errorf("resolve listen addr %s: %w", u.listenAddr, err)
	}
	conn, err := net.ListenUDP("udp", udpAddr)
	if err != nil {
		return fmt.Errorf("listen on %s: %w", u.listenAddr, err)
	}
	defer conn.Close()
	u.listener = conn

	upstreamUDPAddr, err := net.ResolveUDPAddr("udp", u.upstreamAddr)
	if err != nil {
		return fmt.Errorf("resolve upstream %s: %w", u.upstreamAddr, err)
	}

	portStr := strconv.Itoa(u.port)
	buffer := make([]byte, 2048)

	log.Printf("UDP proxy listening on %s, forwarding to %s", u.listenAddr, u.upstreamAddr)

	go func() {
		<-ctx.Done()
		conn.Close()
		// Clean up upstream connections
		u.connMap.Range(func(key, value interface{}) bool {
			if upConn, ok := value.(*net.UDPConn); ok {
				upConn.Close()
			}
			return true
		})
	}()

	for {
		n, srcAddr, err := conn.ReadFromUDP(buffer[:cap(buffer)])
		if err != nil {
			// Check if context cancelled
			select {
			case <-ctx.Done():
				return nil
			default:
				log.Printf("read packet: %v", err)
				continue
			}
		}

		// Copy payload for inspection
		payload := make([]byte, n)
		copy(payload, buffer[:n])

		// Run inspection pipeline asynchronously to avoid blocking the read loop
		go u.inspectAndForward(srcAddr, payload, upstreamUDPAddr, portStr)
	}
}

func (u *UDPProxy) inspectAndForward(srcAddr *net.UDPAddr, payload []byte, upstreamAddr *net.UDPAddr, portStr string) {
	srcIP := srcAddr.IP

	// Step 1: SteamID extraction
	steamID, found := u.pipeline.Mapper.Extract(payload)
	if found {
		u.pipeline.Mapper.Record(srcIP, steamID)
	}

	// Step 2: Priority check (SteamID)
	if found && u.pipeline.Store.IsPriority(steamID) {
		u.forwardPacket(srcAddr, payload, upstreamAddr, portStr)
		return
	}

	// Step 3: Priority check (IP)
	if mappedSteam, ok := u.pipeline.Mapper.SteamForIP(srcIP); ok && u.pipeline.Store.IsPriority(mappedSteam) {
		u.forwardPacket(srcAddr, payload, upstreamAddr, portStr)
		return
	}

	// Step 4: Allowlist
	if u.pipeline.Store.IsIPAllowed(srcIP) {
		u.forwardPacket(srcAddr, payload, upstreamAddr, portStr)
		return
	}

	// Step 5: Block (IP)
	if u.pipeline.Store.IsIPBlocked(srcIP) {
		metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
		log.Printf("[UDP] Drop blocked IP: %s", srcIP.String())
		return
	}

	// Step 6: Block (SteamID)
	if found && u.pipeline.Store.IsSteamIDBlocked(steamID) {
		metrics.SteamIDBlocksTotal.Inc()
		metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
		log.Printf("[UDP] Drop blocked SteamID: %d from %s", steamID, srcIP.String())
		return
	}

	// Step 7: Rate limit
	if !u.pipeline.Limiter.AllowPacket(srcIP, steamID) {
		metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
		log.Printf("[UDP] Drop rate limited: IP=%s SteamID=%d", srcIP.String(), steamID)
		return
	}

	// Step 8: Flood detection
	if u.pipeline.Flood.CheckNew(srcIP, steamID) {
		metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
		log.Printf("[UDP] Drop flood detected: subnet or SteamID from %s", srcIP.String())
		return
	}

	// Step 9: RCON check (skip for UDP)

	// Step 10: Pattern heuristics
	detections := u.pipeline.Patterns.Inspect(srcIP, steamID, payload)
	for _, det := range detections {
		metrics.DetectionEvents.WithLabelValues(det.Heuristic).Inc()
		log.Printf("[UDP] Detection %s: IP=%s SteamID=%d Detail=%s", det.Heuristic, det.IP.String(), det.SteamID, det.Detail)
	}

	// Step 10a: Geo-velocity impossible travel detection
	if p := u.pipeline.GeoVel; p != nil {
		if p.RecordConnect(srcIP, steamID) {
			metrics.GeoVelocityEvents.Inc()
			switch p.Mode() {
			case detect.ModeBlock:
				metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
				log.Printf("[UDP] Drop geo-velocity: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Drop geo-velocity: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
				return
			case detect.ModeMonitor:
				log.Printf("[UDP] Monitor geo-velocity: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Monitor geo-velocity: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
			}
		}
	}

	// Step 10b: Reconnect storm detection
	if p := u.pipeline.Reconnect; p != nil {
		if p.RecordAuth(srcIP, steamID) {
			metrics.ReconnectStorms.Inc()
			switch p.Mode() {
			case detect.ModeBlock:
				metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
				log.Printf("[UDP] Drop reconnect storm: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Drop reconnect storm: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
				return
			case detect.ModeMonitor:
				log.Printf("[UDP] Monitor reconnect storm: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Monitor reconnect storm: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
			}
		}
	}

	// Step 10c: Incomplete handshake flood detection
	if p := u.pipeline.Handshake; p != nil {
		if p.RecordPacket(srcIP) {
			metrics.IncompleteHandshakes.Inc()
			switch p.Mode() {
			case detect.ModeBlock:
				metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
				log.Printf("[UDP] Drop incomplete handshake flood: IP=%s", srcIP.String())
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Drop incomplete handshake flood: IP=%s", srcIP.String()))
				}
				return
			case detect.ModeMonitor:
				log.Printf("[UDP] Monitor incomplete handshake flood: IP=%s", srcIP.String())
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Monitor incomplete handshake flood: IP=%s", srcIP.String()))
				}
			}
		}
	}

	// Step 10d: High-entropy payload detection
	if p := u.pipeline.Entropy; p != nil {
		if p.Analyze(payload) {
			metrics.EntropyDrops.Inc()
			switch p.Mode() {
			case detect.ModeBlock:
				metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
				log.Printf("[UDP] Drop high-entropy payload: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Drop high-entropy payload: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
				return
			case detect.ModeMonitor:
				log.Printf("[UDP] Monitor high-entropy payload: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Monitor high-entropy payload: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
			}
		}
	}

	// Step 10e: IP churn (VPN-hop) detection
	if p := u.pipeline.IPChurn; p != nil {
		if p.RecordConnect(srcIP, steamID) {
			metrics.IPChurnEvents.Inc()
			switch p.Mode() {
			case detect.ModeBlock:
				metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
				log.Printf("[UDP] Drop IP churn: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Drop IP churn: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
				return
			case detect.ModeMonitor:
				log.Printf("[UDP] Monitor IP churn: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Monitor IP churn: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
			}
		}
	}

	// Step 10f: Packet burst detection
	if p := u.pipeline.Burst; p != nil {
		if p.RecordPacket(srcIP) {
			metrics.BurstDrops.Inc()
			switch p.Mode() {
			case detect.ModeBlock:
				metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
				log.Printf("[UDP] Drop packet burst: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Drop packet burst: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
				return
			case detect.ModeMonitor:
				log.Printf("[UDP] Monitor packet burst: IP=%s SteamID=%d", srcIP.String(), steamID)
				if n := u.pipeline.Notifier; n != nil {
					n.Notify(fmt.Sprintf("Monitor packet burst: IP=%s SteamID=%d", srcIP.String(), steamID))
				}
			}
		}
	}

	// Step 10g: UDP amplification detection (record inbound request)
	if p := u.pipeline.Amplify; p != nil {
		p.RecordRequest(srcIP, len(payload))
	}

	// Step 11: Rule engine
	matched, action, ruleID := u.pipeline.Rules.Evaluate(srcIP, steamID, payload, u.port, 0, 0, len(payload), 0)
	if matched {
		metrics.RuleHits.WithLabelValues(ruleID, string(action)).Inc()

		if action == rules.ActionLog || action == rules.ActionAlert {
			log.Printf("[UDP] Rule %s (%s): IP=%s SteamID=%d", ruleID, action, srcIP.String(), steamID)
			u.forwardPacket(srcAddr, payload, upstreamAddr, portStr)
			return
		}

		if action == rules.ActionDrop {
			metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
			log.Printf("[UDP] Rule %s (DROP): IP=%s SteamID=%d", ruleID, srcIP.String(), steamID)
			return
		}

		if action == rules.ActionBlock {
			u.pipeline.Store.BlockIP(srcIP, 24*time.Hour, "RULE_"+ruleID)
			metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
			log.Printf("[UDP] Rule %s (BLOCK): IP=%s blocked for 24h, SteamID=%d", ruleID, srcIP.String(), steamID)
			return
		}

		if action == rules.ActionThrottle {
			u.pipeline.Store.ThrottleIP(srcIP, 0.5, 5*time.Minute)
			log.Printf("[UDP] Rule %s (THROTTLE): IP=%s throttled, SteamID=%d", ruleID, srcIP.String(), steamID)
		}
	}

	// Step 12: Forward
	u.forwardPacket(srcAddr, payload, upstreamAddr, portStr)
}

func (u *UDPProxy) forwardPacket(srcAddr *net.UDPAddr, payload []byte, upstreamAddr *net.UDPAddr, portStr string) {
	// Get or create upstream connection for this client
	srcKey := srcAddr.String()
	val, _ := u.connMap.LoadOrStore(srcKey, (*net.UDPConn)(nil))

	var upConn *net.UDPConn
	if val != nil {
		upConn = val.(*net.UDPConn)
	}

	// If no connection exists, create one
	if upConn == nil {
		var err error
		upConn, err = net.DialUDP("udp", nil, upstreamAddr)
		if err != nil {
			log.Printf("[UDP] Failed to dial upstream %s: %v", upstreamAddr.String(), err)
			return
		}
		u.connMap.Store(srcKey, upConn)

		// Track client addr keyed by upstream conn's local addr for response routing
		u.clientMap.Store(upConn.LocalAddr().String(), srcAddr)

		// Start goroutine to read responses and forward back to client
		go u.readUpstreamResponses(srcAddr, upConn, portStr)
	}

	// Send packet to upstream
	_, err := upConn.Write(payload)
	if err != nil {
		log.Printf("[UDP] Write to upstream failed: %v", err)
		upConn.Close()
		u.connMap.Delete(srcKey)
		return
	}

	metrics.PacketsTotal.WithLabelValues(portStr, "forward").Inc()
}

func (u *UDPProxy) readUpstreamResponses(dstAddr *net.UDPAddr, upConn *net.UDPConn, portStr string) {
	buffer := make([]byte, 2048)

	for {
		n, err := upConn.Read(buffer)
		if err != nil {
			upConn.Close()
			u.connMap.Delete(dstAddr.String())
			return
		}

		// Amplification: record response bytes; flag if ratio exceeds threshold
		if p := u.pipeline.Amplify; p != nil {
			if p.RecordResponse(dstAddr.IP, n) {
				metrics.AmplificationBlocks.Inc()
				switch p.Mode() {
				case detect.ModeBlock:
					metrics.PacketsTotal.WithLabelValues(portStr, "drop").Inc()
					log.Printf("[UDP] Drop amplification response: client=%s", dstAddr.String())
					continue
				case detect.ModeMonitor:
					log.Printf("[UDP] Monitor amplification response: client=%s", dstAddr.String())
				}
			}
		}

		if u.listener != nil {
			if _, werr := u.listener.WriteToUDP(buffer[:n], dstAddr); werr != nil {
				log.Printf("[UDP] Write response to client %s failed: %v", dstAddr.String(), werr)
			} else {
				metrics.PacketsTotal.WithLabelValues(portStr, "response").Inc()
			}
		}
	}
}
