package config

import (
	"fmt"
	"strings"
	"testing"
	"time"
)

const (
	erpSecret = "erp-secret-value"
	apiKey    = "sync-api-key-value"
)

func env(overrides map[string]string) func(string) string {
	values := map[string]string{
		"ERP_BASE_URL":      "http://mock-erp:8080/",
		"ERP_CLIENT_ID":     "erp-sync",
		"ERP_CLIENT_SECRET": erpSecret,
		"APP_BASE_URL":      "http://web:8080/",
		"SYNC_API_KEY":      apiKey,
	}
	for k, v := range overrides {
		values[k] = v
	}
	return func(k string) string { return values[k] }
}

func TestTheRequiredValuesAndTheDefaults(t *testing.T) {
	cfg, err := Load(env(nil))

	if err != nil {
		t.Fatal(err)
	}
	if cfg.ERPBaseURL != "http://mock-erp:8080/" || cfg.TokenURL != "http://mock-erp:8080/oauth/token" || cfg.ClientID != "erp-sync" ||
		cfg.ClientSecret != erpSecret || cfg.AppBaseURL != "http://web:8080/" || cfg.APIKey != apiKey {
		t.Fatalf("got %#v", cfg)
	}
	if cfg.Interval != 0 || cfg.RunTimeout != 4*time.Minute || cfg.Workers != 4 || cfg.PageSize != 1000 || cfg.Scope != "" {
		t.Fatalf("defaults %+v", cfg)
	}
}

func TestEveryValueCanBeSet(t *testing.T) {
	cfg, err := Load(env(map[string]string{
		"ERP_BASE_URL": "https://erp.example.com/api", "ERP_TOKEN_URL": "https://login.example.com/token", "ERP_SCOPE": "api://erp/.default",
		"SYNC_INTERVAL": "5m", "SYNC_RUN_TIMEOUT": "90s", "SYNC_WORKERS": "8", "SYNC_PAGE_SIZE": "250",
	}))

	if err != nil {
		t.Fatal(err)
	}
	if cfg.ERPBaseURL != "https://erp.example.com/api/" || cfg.TokenURL != "https://login.example.com/token" || cfg.Scope != "api://erp/.default" ||
		cfg.Interval != 5*time.Minute || cfg.RunTimeout != 90*time.Second || cfg.Workers != 8 || cfg.PageSize != 250 {
		t.Fatalf("got %+v", cfg)
	}
}

func TestMissingValuesAreNamedAllAtOnce(t *testing.T) {
	_, err := Load(func(string) string { return "" })

	if err == nil {
		t.Fatal("no error")
	}
	for _, name := range []string{"ERP_BASE_URL", "ERP_CLIENT_ID", "ERP_CLIENT_SECRET", "APP_BASE_URL", "SYNC_API_KEY"} {
		if !strings.Contains(err.Error(), name) {
			t.Errorf("error does not name %s: %v", name, err)
		}
	}
}

func TestWrongValuesAreRefusedWithoutRepeatingThem(t *testing.T) {
	for name, value := range map[string]string{
		"ERP_BASE_URL":     "mock-erp:8080",
		"ERP_TOKEN_URL":    "ftp://erp/token",
		"APP_BASE_URL":     "/relative",
		"SYNC_INTERVAL":    "often",
		"SYNC_RUN_TIMEOUT": "0s",
		"SYNC_WORKERS":     "0",
		"SYNC_PAGE_SIZE":   "1001",
	} {
		_, err := Load(env(map[string]string{name: value}))

		if err == nil || !strings.Contains(err.Error(), name) || strings.Contains(err.Error(), value) {
			t.Errorf("%s=%q: got %v", name, value, err)
		}
	}
}

func TestARunMustEndBeforeTheNextStarts(t *testing.T) {
	_, err := Load(env(map[string]string{"SYNC_INTERVAL": "1m", "SYNC_RUN_TIMEOUT": "2m"}))

	if err == nil || !strings.Contains(err.Error(), "SYNC_RUN_TIMEOUT") {
		t.Fatalf("got %v", err)
	}
}

func TestSecretsAreNeverInTheConfigsText(t *testing.T) {
	cfg, err := Load(env(nil))
	if err != nil {
		t.Fatal(err)
	}

	for _, text := range []string{fmt.Sprint(cfg), fmt.Sprintf("%+v", cfg), fmt.Sprintf("%#v", cfg), fmt.Sprint(&cfg)} {
		if strings.Contains(text, erpSecret) || strings.Contains(text, apiKey) {
			t.Fatalf("secret in %q", text)
		}
	}
}
