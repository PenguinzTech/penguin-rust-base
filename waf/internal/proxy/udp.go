package proxy

import (
	"context"
	"fmt"
	"log"
	"net"
	"strconv"
	"sync"
	"time"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/metrics"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/rules"
)

// UDPProxy proxies UDP traffic with WAF inspection.
type UDPProxy struct {
	listenAddr   string
	upstreamAddr string
	pipeline     *Pipeline
	port         int
	connMap      sync.Map // key: string(srcAddr) → *net.UDPConn
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

		// In a real implementation, we would write back to the client via the original listener.
		// For now, we log it. A full implementation would require storing the listener conn.
		_ = n
		_ = portStr
	}
}
