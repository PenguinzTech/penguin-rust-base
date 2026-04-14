package cfg

import (
	"net"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	"github.com/penguintechinc/penguin-rust-base/waf/internal/detect"
	"github.com/penguintechinc/penguin-rust-base/waf/internal/state"
)

func TestParseUsersOwnerID(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	// Create test data
	data := []byte(`
ownerid 76561198123456789 "Admin"
moderatorid 76561198987654321 "Mod"
`)

	poller.parseUsers(data)

	// Both should be marked as priority
	if !store.IsPriority(76561198123456789) {
		t.Error("parseUsers() should set ownerid as priority")
	}

	if !store.IsPriority(76561198987654321) {
		t.Error("parseUsers() should set moderatorid as priority")
	}
}

func TestParseUsersSkipsComments(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	data := []byte(`
// This is a comment
ownerid 76561198123456789 "Admin"
// Another comment
`)

	poller.parseUsers(data)

	if !store.IsPriority(76561198123456789) {
		t.Error("parseUsers() should skip comments and process ownerid")
	}
}

func TestParseUsersSkipsEmptyLines(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	data := []byte(`

ownerid 76561198123456789 "Admin"

`)

	poller.parseUsers(data)

	if !store.IsPriority(76561198123456789) {
		t.Error("parseUsers() should skip empty lines")
	}
}

func TestParseUsersIgnoresInvalidSteamID(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	data := []byte(`
ownerid notasteamid "Admin"
ownerid 76561198123456789 "Admin"
`)

	poller.parseUsers(data)

	// Should not crash and should process valid ID
	if !store.IsPriority(76561198123456789) {
		t.Error("parseUsers() should process valid ID despite invalid entries")
	}
}

func TestParseUsersIgnoresUnknownLineType(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	data := []byte(`
ownerid 76561198123456789 "Admin"
unknowntype 76561198987654321 "Unknown"
moderatorid 76561198111222333 "Mod"
`)

	poller.parseUsers(data)

	if !store.IsPriority(76561198123456789) {
		t.Error("ownerid should be processed")
	}

	if !store.IsPriority(76561198111222333) {
		t.Error("moderatorid should be processed")
	}

	// unknowntype should be ignored
	if store.IsPriority(76561198987654321) {
		t.Error("unknowntype should be ignored")
	}
}

func TestParseBansBlocks(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`
banid 76561198123456789 "PlayerName" "Reason" ` + futureExpiry)

	poller.parseBans(data)

	if !store.IsSteamIDBlocked(76561198123456789) {
		t.Error("parseBans() should block steamid")
	}
}

func TestParseBansExpiredBan(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	// Set expiry in the past
	pastExpiry := strconv.FormatInt(time.Now().Unix()-3600, 10)
	data := []byte(`
banid 76561198123456789 "PlayerName" "Reason" ` + pastExpiry)

	poller.parseBans(data)

	// Expired bans should not block
	if store.IsSteamIDBlocked(76561198123456789) {
		t.Error("parseBans() should unblock expired bans")
	}
}

func TestParseBansPermanentBan(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	// Expiry of 0 means permanent
	data := []byte(`
banid 76561198123456789 "PlayerName" "Reason" 0`)

	poller.parseBans(data)

	if !store.IsSteamIDBlocked(76561198123456789) {
		t.Error("parseBans() should block permanent bans")
	}
}

func TestParseBansPriorityUnset(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	steamID := uint64(76561198123456789)

	// Mark as priority first
	store.SetPriority(steamID)

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`
banid 76561198123456789 "PlayerName" "Reason" ` + futureExpiry)

	poller.parseBans(data)

	// Should be unset from priority but still blocked
	if store.IsPriority(steamID) {
		t.Error("parseBans() should unset priority when banning")
	}

	if !store.IsSteamIDBlocked(steamID) {
		t.Error("parseBans() should block despite being priority")
	}
}

func TestParseBansBlocksAssociatedIPs(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	steamID := uint64(76561198123456789)
	ips := []net.IP{
		net.ParseIP("192.168.1.1"),
		net.ParseIP("10.0.0.1"),
	}

	// Record IPs for the SteamID
	for _, ip := range ips {
		mapper.Record(ip, steamID)
	}

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`
banid 76561198123456789 "PlayerName" "Reason" ` + futureExpiry)

	poller.parseBans(data)

	// All IPs should be blocked
	for _, ip := range ips {
		if !store.IsIPBlocked(ip) {
			t.Errorf("parseBans() should block IP %s associated with steamid", ip.String())
		}
	}
}

func TestParseBansSkipsComments(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`
// This is a comment
banid 76561198123456789 "PlayerName" "Reason" ` + futureExpiry + `
// Another comment
`)

	poller.parseBans(data)

	if !store.IsSteamIDBlocked(76561198123456789) {
		t.Error("parseBans() should skip comments and process ban")
	}
}

func TestParseBansSkipsEmptyLines(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`

banid 76561198123456789 "PlayerName" "Reason" ` + futureExpiry + `

`)

	poller.parseBans(data)

	if !store.IsSteamIDBlocked(76561198123456789) {
		t.Error("parseBans() should skip empty lines and process ban")
	}
}

func TestParseBansInvalidSteamID(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`
banid notasteamid "PlayerName" "Reason" ` + futureExpiry + `
banid 76561198123456789 "PlayerName" "Reason" ` + futureExpiry)

	poller.parseBans(data)

	// Should skip invalid and process valid
	if !store.IsSteamIDBlocked(76561198123456789) {
		t.Error("parseBans() should process valid steamid despite invalid entries")
	}
}

func TestParseBansMultipleBans(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`
banid 76561198111111111 "Player1" "Reason1" ` + futureExpiry + `
banid 76561198222222222 "Player2" "Reason2" ` + futureExpiry + `
banid 76561198333333333 "Player3" "Reason3" ` + futureExpiry)

	poller.parseBans(data)

	steamIDs := []uint64{
		76561198111111111,
		76561198222222222,
		76561198333333333,
	}

	for _, id := range steamIDs {
		if !store.IsSteamIDBlocked(id) {
			t.Errorf("parseBans() should block steamid %d", id)
		}
	}
}

func TestPollerStartStop(t *testing.T) {
	tmpDir := t.TempDir()
	usersPath := filepath.Join(tmpDir, "users.cfg")
	bansPath := filepath.Join(tmpDir, "bans.cfg")

	// Create empty config files
	os.WriteFile(usersPath, []byte{}, 0644)
	os.WriteFile(bansPath, []byte{}, 0644)

	store := state.New()
	mapper := detect.NewMapper()
	testPoller := New(usersPath, bansPath, 100*time.Millisecond, store, mapper)

	// Test that poller can start and stop without issues
	// (We won't actually run it, just verify construction)
	if testPoller == nil {
		t.Error("New() should create a poller")
	}
}

func TestPollerFileReading(t *testing.T) {
	tmpDir := t.TempDir()
	usersPath := filepath.Join(tmpDir, "users.cfg")
	bansPath := filepath.Join(tmpDir, "bans.cfg")

	// Create config files with content
	usersContent := []byte("ownerid 76561198123456789 \"Admin\"\n")
	os.WriteFile(usersPath, usersContent, 0644)

	store := state.New()
	mapper := detect.NewMapper()
	poller := New(usersPath, bansPath, 100*time.Millisecond, store, mapper)

	// Call poll directly
	poller.poll()

	// Verify content was parsed
	if !store.IsPriority(76561198123456789) {
		t.Error("poller.poll() should parse users config file")
	}
}

func TestParseBansWithQuotedFields(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	futureExpiry := strconv.FormatInt(time.Now().Unix()+3600, 10)
	data := []byte(`
banid 76561198123456789 "Player Name With Spaces" "Ban reason with spaces" ` + futureExpiry)

	poller.parseBans(data)

	if !store.IsSteamIDBlocked(76561198123456789) {
		t.Error("parseBans() should handle quoted fields correctly")
	}
}

func TestParseUsersMinimalFormat(t *testing.T) {
	store := state.New()
	mapper := detect.NewMapper()
	poller := New("", "", time.Second, store, mapper)

	// Minimal format with just type and steamid
	data := []byte("ownerid 76561198123456789\n")

	poller.parseUsers(data)

	if !store.IsPriority(76561198123456789) {
		t.Error("parseUsers() should handle minimal format")
	}
}

func TestParseBansExpiryHandling(t *testing.T) {
	// Test with various expiry values
	tests := []struct {
		name       string
		expiryStr  string
		shouldBlock bool
	}{
		{
			name:       "permanent ban",
			expiryStr:  "0",
			shouldBlock: true,
		},
		{
			name:       "future expiry",
			expiryStr:  strconv.FormatInt(time.Now().Unix()+3600, 10),
			shouldBlock: true,
		},
		{
			name:       "past expiry",
			expiryStr:  strconv.FormatInt(time.Now().Unix()-3600, 10),
			shouldBlock: false,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			store := state.New()
			mapper := detect.NewMapper()
			poller := New("", "", time.Second, store, mapper)

			steamID := uint64(76561198123456789 + uint64(len(tt.name)))
			data := []byte(`
banid ` + strconv.FormatUint(steamID, 10) + ` "Player" "Reason" ` + tt.expiryStr)

			poller.parseBans(data)

			if store.IsSteamIDBlocked(steamID) != tt.shouldBlock {
				t.Errorf("Ban with expiry %s should block=%v", tt.expiryStr, tt.shouldBlock)
			}
		})
	}
}
