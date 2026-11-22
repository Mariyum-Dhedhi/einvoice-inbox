// Package config reads the sync's settings from environment variables only (secrets included: never a file in the
// repository). Errors name the variable, never its value.
package config

import (
	"errors"
	"fmt"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// Config is everything a run needs. ClientSecret and APIKey never leave through String or GoString.
type Config struct {
	ERPBaseURL   string        // ERP_BASE_URL, required
	TokenURL     string        // ERP_TOKEN_URL, default ERP_BASE_URL + oauth/token
	ClientID     string        // ERP_CLIENT_ID, required
	ClientSecret string        // ERP_CLIENT_SECRET, required
	Scope        string        // ERP_SCOPE, optional
	AppBaseURL   string        // APP_BASE_URL, required
	APIKey       string        // SYNC_API_KEY, required
	Interval     time.Duration // SYNC_INTERVAL: 0 (default) runs once; else a run every interval
	RunTimeout   time.Duration // SYNC_RUN_TIMEOUT, default 4m, shorter than the interval
	Workers      int           // SYNC_WORKERS, 1 to 32, default 4
	PageSize     int           // SYNC_PAGE_SIZE, 1 to 1000, default 1000
}

func (c Config) String() string {
	return fmt.Sprintf("config{ERP:%s Token:%s Client:%s Secret:[hidden] Scope:%s App:%s APIKey:[hidden] Interval:%s RunTimeout:%s Workers:%d PageSize:%d}",
		c.ERPBaseURL, c.TokenURL, c.ClientID, c.Scope, c.AppBaseURL, c.Interval, c.RunTimeout, c.Workers, c.PageSize)
}

func (c Config) GoString() string { return c.String() }

// Load reads the settings through getenv (os.Getenv in main). Every problem is named at once.
func Load(getenv func(string) string) (Config, error) {
	var problems []string
	required := func(name string) string {
		v := strings.TrimSpace(getenv(name))
		if v == "" {
			problems = append(problems, name+" is required")
		}
		return v
	}
	absolute := func(name, value string) string {
		if value == "" {
			return ""
		}
		u, err := url.Parse(value)
		if err != nil || !u.IsAbs() || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
			problems = append(problems, name+" must be an absolute http or https URL")
			return ""
		}
		return value
	}
	duration := func(name string, fallback time.Duration, allowZero bool) time.Duration {
		v := strings.TrimSpace(getenv(name))
		if v == "" {
			return fallback
		}
		d, err := time.ParseDuration(v)
		if err != nil || d < 0 || (d == 0 && !allowZero) {
			problems = append(problems, name+" must be a duration such as 45s or 5m"+map[bool]string{true: " (0 for once)", false: " above 0"}[allowZero])
			return fallback
		}
		return d
	}
	number := func(name string, fallback, low, high int) int {
		v := strings.TrimSpace(getenv(name))
		if v == "" {
			return fallback
		}
		n, err := strconv.Atoi(v)
		if err != nil || n < low || n > high {
			problems = append(problems, fmt.Sprintf("%s must be a whole number from %d to %d", name, low, high))
			return fallback
		}
		return n
	}

	cfg := Config{
		ERPBaseURL:   withSlash(absolute("ERP_BASE_URL", required("ERP_BASE_URL"))),
		ClientID:     required("ERP_CLIENT_ID"),
		ClientSecret: required("ERP_CLIENT_SECRET"),
		Scope:        strings.TrimSpace(getenv("ERP_SCOPE")),
		AppBaseURL:   withSlash(absolute("APP_BASE_URL", required("APP_BASE_URL"))),
		APIKey:       required("SYNC_API_KEY"),
		Interval:     duration("SYNC_INTERVAL", 0, true),
		RunTimeout:   duration("SYNC_RUN_TIMEOUT", 4*time.Minute, false),
		Workers:      number("SYNC_WORKERS", 4, 1, 32),
		PageSize:     number("SYNC_PAGE_SIZE", 1000, 1, 1000),
	}
	cfg.TokenURL = absolute("ERP_TOKEN_URL", strings.TrimSpace(getenv("ERP_TOKEN_URL")))
	if cfg.TokenURL == "" && cfg.ERPBaseURL != "" {
		cfg.TokenURL = cfg.ERPBaseURL + "oauth/token"
	}
	if cfg.Interval > 0 && cfg.RunTimeout >= cfg.Interval {
		problems = append(problems, "SYNC_RUN_TIMEOUT must be shorter than SYNC_INTERVAL: a run ends before the next starts")
	}
	if len(problems) > 0 {
		return Config{}, errors.New("settings: " + strings.Join(problems, "; "))
	}
	return cfg, nil
}

func withSlash(u string) string {
	if u == "" || strings.HasSuffix(u, "/") {
		return u
	}
	return u + "/"
}
