package cfg

import (
	"bytes"
	"context"
	"log"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

type Poller struct {
	usersCfgPath string
	bansCfgPath  string
	interval     time.Duration
	store        *state.Store
	mapper       *detect.Mapper
	usersMtime   time.Time
	bansMtime    time.Time
}

func New(usersCfgPath, bansCfgPath string, interval time.Duration, store *state.Store, mapper *detect.Mapper) *Poller {
	return &Poller{
		usersCfgPath: usersCfgPath,
		bansCfgPath:  bansCfgPath,
		interval:     interval,
		store:        store,
		mapper:       mapper,
	}
}

func (p *Poller) Start(ctx context.Context) {
	ticker := time.NewTicker(p.interval)
	defer ticker.Stop()

	// Poll immediately on startup
	p.poll()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			p.poll()
		}
	}
}

func (p *Poller) poll() {
	// Check and load users config
	info, err := os.Stat(p.usersCfgPath)
	if err != nil {
		if !os.IsNotExist(err) {
			log.Printf("[CFG] Error stat users config: %v", err)
		}
	} else {
		mtime := info.ModTime()
		if mtime.After(p.usersMtime) {
			data, err := os.ReadFile(p.usersCfgPath)
			if err != nil {
				log.Printf("[CFG] Error reading users config: %v", err)
			} else {
				p.usersMtime = mtime
				p.parseUsers(data)
			}
		}
	}

	// Check and load bans config
	info, err = os.Stat(p.bansCfgPath)
	if err != nil {
		if !os.IsNotExist(err) {
			log.Printf("[CFG] Error stat bans config: %v", err)
		}
	} else {
		mtime := info.ModTime()
		if mtime.After(p.bansMtime) {
			data, err := os.ReadFile(p.bansCfgPath)
			if err != nil {
				log.Printf("[CFG] Error reading bans config: %v", err)
			} else {
				p.bansMtime = mtime
				p.parseBans(data)
			}
		}
	}
}

func (p *Poller) parseUsers(data []byte) {
	lines := bytes.Split(data, []byte("\n"))

	for _, line := range lines {
		line = bytes.TrimSpace(line)
		if len(line) == 0 || line[0] == '/' && len(line) > 1 && line[1] == '/' {
			// Skip empty lines and comments
			continue
		}

		fields := bytes.Fields(line)
		if len(fields) < 2 {
			continue
		}

		lineType := string(fields[0])
		if lineType != "ownerid" && lineType != "moderatorid" {
			continue
		}

		// Parse steam ID (field 1)
		steamIDStr := string(fields[1])
		steamID, err := strconv.ParseUint(steamIDStr, 10, 64)
		if err != nil {
			log.Printf("[CFG] Error parsing steam id %s: %v", steamIDStr, err)
			continue
		}

		p.store.SetPriority(steamID)
		log.Printf("[CFG] Set priority: %s (steamid=%d)", lineType, steamID)
	}
}

func (p *Poller) parseBans(data []byte) {
	lines := bytes.Split(data, []byte("\n"))

	for _, line := range lines {
		line = bytes.TrimSpace(line)
		if len(line) == 0 || line[0] == '/' && len(line) > 1 && line[1] == '/' {
			// Skip empty lines and comments
			continue
		}

		// Parse banid line: banid <steamid> "Name" "reason" <expiry>
		if !bytes.HasPrefix(line, []byte("banid ")) {
			continue
		}

		// Remove "banid " prefix and split by whitespace and quotes
		line = bytes.TrimPrefix(line, []byte("banid "))

		// Extract steam ID (first field)
		spaceIdx := bytes.IndexByte(line, ' ')
		if spaceIdx == -1 {
			continue
		}

		steamIDStr := string(line[:spaceIdx])
		steamID, err := strconv.ParseUint(steamIDStr, 10, 64)
		if err != nil {
			log.Printf("[CFG] Error parsing ban steamid %s: %v", steamIDStr, err)
			continue
		}

		// Extract expiry (last field, should be a number)
		// Format: "Name" "reason" <expiry>
		line = bytes.TrimSpace(line[spaceIdx:])

		// Skip quoted name and reason fields
		var inQuote bool
		var fieldIdx int
		for i := 0; i < len(line); i++ {
			ch := line[i]
			if ch == '"' {
				inQuote = !inQuote
			} else if ch == ' ' && !inQuote {
				fieldIdx++
			}
		}

		// Extract the expiry value (last field)
		expiryStr := ""
		{
			fields := strings.Fields(string(line))
			if len(fields) > 0 {
				expiryStr = fields[len(fields)-1]
			}
		}

		expiry := int64(0)
		if expiryStr != "" {
			expiryVal, err := strconv.ParseInt(expiryStr, 10, 64)
			if err == nil {
				expiry = expiryVal
			}
		}

		// Check if ban has expired
		if expiry > 0 && time.Now().Unix() > expiry {
			p.store.UnblockSteamID(steamID)
			log.Printf("[CFG] Unblocked expired ban: steamid=%d", steamID)
			continue
		}

		// Check if this steamid is in priority list
		if p.store.IsPriority(steamID) {
			log.Printf("[CFG] WARN: Ban for priority identity will be applied: steamid=%d (ban takes precedence)", steamID)
			p.store.UnsetPriority(steamID)
		}

		// Block the SteamID
		var duration time.Duration
		if expiry > 0 {
			duration = time.Until(time.Unix(expiry, 0))
			if duration < 0 {
				duration = 0
			}
		}

		p.store.BlockSteamID(steamID, duration, "config ban")

		// Also block all known IPs for this SteamID
		ips := p.mapper.IPsForSteam(steamID)
		for _, ip := range ips {
			p.store.BlockIP(ip, duration, "config ban for steamid "+steamIDStr)
		}

		log.Printf("[CFG] Blocked steamid=%d with %d ip(s)", steamID, len(ips))
	}
}
